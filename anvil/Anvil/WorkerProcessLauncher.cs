using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Anvil;

internal static class AnvilWorkerPreset
{
    public const int Executors = 4;
    public const int ClaimBatch = 8;
    public const string Profile = "direct";
}

/// <summary>
/// Owns the real child processes used by the lab. Worker-count changes are reconciled in place: existing
/// healthy processes remain, excess processes drain gracefully, and only missing processes are spawned.
/// </summary>
public sealed class WorkerProcessLauncher(string runId, string schema, string provider, string namespaceName) : IDisposable
{
    private const int ExitedWorkerRetention = 3;

    private readonly ConcurrentDictionary<int, ManagedWorker> _workers = new();
    private readonly string _runId = runId;
    private int _sequence;

    public WorkerSnapshot Spawn()
    {
        var ordinal = Interlocked.Increment(ref _sequence);
        var name = $"worker-{ordinal}";
        var start = BuildWorkerStartInfo(name);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var managed = new ManagedWorker(ordinal, name, process);

        process.OutputDataReceived += (_, e) => managed.ObserveOutput(e.Data);
        process.ErrorDataReceived += (_, e) => managed.ObserveError(e.Data);
        process.Exited += (_, _) => managed.ObserveExit();

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start worker process '{start.FileName}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _workers[ordinal] = managed;
        return managed.ToSnapshot();
    }

    public IReadOnlyList<WorkerSnapshot> SetTargetCount(int target)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(target, 0);

        var active = _workers.Values.Where(worker => worker.IsAlive && !worker.DrainRequested).OrderBy(worker => worker.Id).ToArray();

        if (active.Length < target)
        {
            for (var i = active.Length; i < target; i++)
            {
                Spawn();
            }
        }
        else if (active.Length > target)
        {
            foreach (var worker in active.OrderByDescending(worker => worker.Id).Take(active.Length - target))
            {
                worker.RequestStop();
            }
        }

        return Snapshot();
    }

    public bool Crash(int id) => _workers.TryGetValue(id, out var managed) && managed.Kill();

    public bool Stop(int id) => _workers.TryGetValue(id, out var managed) && managed.RequestStop();

    internal WorkerSnapshot? CrashOneHealthy()
    {
        var candidates = Snapshot()
            .Where(worker => worker.ProcessStatus == "running" && !worker.CrashRequested && !worker.DrainRequested)
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        var selected = candidates[Random.Shared.Next(candidates.Length)];
        return Crash(selected.Id) ? _workers[selected.Id].ToSnapshot() : null;
    }

    public IReadOnlyList<WorkerSnapshot> Snapshot()
    {
        var snapshots = _workers.Values.Select(worker => worker.ToSnapshot()).OrderBy(worker => worker.Id).ToList();
        var expired = snapshots
            .Where(worker => worker.ProcessStatus == "exited")
            .OrderByDescending(worker => worker.ExitedAtUtc ?? DateTime.MinValue)
            .Skip(ExitedWorkerRetention)
            .ToArray();

        foreach (var worker in expired)
        {
            if (_workers.TryRemove(worker.Id, out var removed))
            {
                removed.DisposeProcess();
            }
            snapshots.Remove(worker);
        }

        return snapshots;
    }

    private ProcessStartInfo BuildWorkerStartInfo(string name)
    {
        var start = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var processPath = Environment.ProcessPath!;
        start.FileName = processPath;
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        AddArgument(start, "--role", "worker");
        AddArgument(start, "--worker-name", name);
        AddArgument(start, "--run", _runId);
        AddArgument(start, "--schema", schema);
        AddArgument(start, "--provider", provider);
        AddArgument(start, "--namespace", namespaceName);
        AddArgument(start, "--executors", AnvilWorkerPreset.Executors.ToString(CultureInfo.InvariantCulture));
        AddArgument(start, "--profile", AnvilWorkerPreset.Profile);
        AddArgument(start, "--claim-batch", AnvilWorkerPreset.ClaimBatch.ToString(CultureInfo.InvariantCulture));
        AddArgument(start, "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return start;
    }

    private static void AddArgument(ProcessStartInfo start, string name, string value)
    {
        start.ArgumentList.Add(name);
        start.ArgumentList.Add(value);
    }

    public void Dispose()
    {
        foreach (var managed in _workers.Values)
        {
            managed.TerminateForShutdown();
            managed.DisposeProcess();
        }
    }

    private sealed class ManagedWorker(int id, string name, Process process)
    {
        private readonly Process _process = process;
        private readonly object _gate = new();
        private bool _crashRequested;
        private bool _drainRequested;
        private DateTime? _exitedAtUtc;
        private int? _exitCode;
        private string? _lastErrorLine;

        public int Id { get; } = id;
        public string Name { get; } = name;

        public bool DrainRequested
        {
            get
            {
                lock (_gate)
                {
                    return _drainRequested;
                }
            }
        }

        public bool IsAlive
        {
            get
            {
                try
                {
                    return !_process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public void ObserveOutput(string? line)
        {
            if (
                !string.IsNullOrWhiteSpace(line)
                && (
                    line.Contains("warn", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                SetLastError(line);
            }
        }

        public void ObserveError(string? line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                SetLastError(line);
            }
        }

        private void SetLastError(string line)
        {
            lock (_gate)
            {
                _lastErrorLine = line.Length > 240 ? line[..240] : line;
            }
        }

        public bool Kill()
        {
            lock (_gate)
            {
                if (_crashRequested || _drainRequested)
                {
                    return false;
                }
                _crashRequested = true;
            }

            try
            {
                if (_process.HasExited)
                {
                    ResetCrashRequested();
                    return false;
                }

                _process.Kill(entireProcessTree: true);
                if (_process.WaitForExit(750))
                {
                    ObserveExit();
                }
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ResetCrashRequested();
                return false;
            }
        }

        public bool RequestStop()
        {
            lock (_gate)
            {
                if (_drainRequested || _crashRequested)
                {
                    return false;
                }
                _drainRequested = true;
            }

            try
            {
                if (_process.HasExited)
                {
                    ResetDrainRequested();
                    return false;
                }

                _process.StandardInput.WriteLine("stop");
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                ResetDrainRequested();
                return false;
            }
        }

        private void ResetCrashRequested()
        {
            lock (_gate)
            {
                _crashRequested = false;
            }
        }

        private void ResetDrainRequested()
        {
            lock (_gate)
            {
                _drainRequested = false;
            }
        }

        public void ObserveExit()
        {
            lock (_gate)
            {
                if (_exitedAtUtc is not null)
                {
                    return;
                }

                _exitedAtUtc = DateTime.UtcNow;
                try
                {
                    _exitCode = _process.ExitCode;
                }
                catch (InvalidOperationException) { }
            }
        }

        public WorkerSnapshot ToSnapshot()
        {
            int? pid = null;
            var exited = true;
            try
            {
                pid = _process.Id;
                exited = _process.HasExited;
            }
            catch (InvalidOperationException) { }

            if (exited)
            {
                ObserveExit();
            }

            lock (_gate)
            {
                return new WorkerSnapshot(
                    Id,
                    Name,
                    pid,
                    exited ? "exited" : "running",
                    _crashRequested,
                    _drainRequested,
                    _exitCode,
                    _exitedAtUtc,
                    _lastErrorLine
                );
            }
        }

        public void TerminateForShutdown()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
        }

        public void DisposeProcess()
        {
            try
            {
                _process.Dispose();
            }
            catch (InvalidOperationException) { }
        }
    }
}

public sealed record WorkerSnapshot(
    int Id,
    string Name,
    int? Pid,
    string ProcessStatus,
    bool CrashRequested,
    bool DrainRequested,
    int? ExitCode,
    DateTime? ExitedAtUtc,
    string? LastErrorLine
);

/// <summary>Worker-role lifecycle watchers shared by framework-dependent and native child processes.</summary>
internal static class WorkerLifecycle
{
    public static void WatchForDrain(IHostApplicationLifetime lifetime, string workerName) =>
        _ = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (string.Equals(line.Trim(), "stop", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[{workerName}] drain requested; stopping gracefully.");
                    lifetime.StopApplication();
                    return;
                }
            }
        });

    public static void WatchParent(int parentPid, string workerName) =>
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    bool parentAlive;
                    try
                    {
                        using var parent = Process.GetProcessById(parentPid);
                        parentAlive = !parent.HasExited;
                    }
                    catch
                    {
                        parentAlive = false;
                    }

                    if (!parentAlive)
                    {
                        Console.WriteLine($"[{workerName}] dashboard {parentPid} is gone; exiting.");
                        Environment.Exit(0);
                    }
                }
            }
            catch
            {
                Environment.Exit(0);
            }
        });
}
