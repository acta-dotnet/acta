using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Acta.Verify;

internal sealed class OwnedWorker : IDisposable
{
    private readonly Process _process;
    private readonly Task _output;
    private readonly Task _error;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _errorTail = new();
    private bool _contractRejected;
    private int _droppedWakeups;

    private OwnedWorker(Process process)
    {
        _process = process;
        _output = DrainAsync(process.StandardOutput, true);
        _error = DrainAsync(process.StandardError, false);
    }

    /// <summary>
    /// The heartbeat fault and recovery cases give their workers: a killed worker's lease (four heartbeats) expires
    /// inside their recovery bound, where the product default would hold it for minutes.
    /// </summary>
    public const int FaultHeartbeatSeconds = 1;

    private static readonly JsonNode Runtime = JsonNode.Parse(Protocol.Read("full.v1.json"))!["measurement"]!["runtime"]!;

    /// <summary>
    /// How long a started process may take to report ready, the protocol's <c>measurement.runtime.worker_ready_seconds</c>.
    /// A start queued behind a busy database is slow, not a harness failure.
    /// </summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(Runtime["worker_ready_seconds"]!.GetValue<double>());

    /// <summary>
    /// The logical processors every owned worker runs on: a hybrid CPU's performance cores when the protocol's
    /// <c>measurement.runtime.pin_workers_to_performance_cores</c> asks for it, else null and the scheduler places them.
    /// Left to it, Windows runs whole worker processes on efficiency cores, so the same trial lands in a slow or a fast mode.
    /// </summary>
    public static readonly ulong? Affinity = Runtime["pin_workers_to_performance_cores"]!.GetValue<bool>()
        ? HardwareDescription.PerformanceMask()
        : null;

    public int Id => _process.Id;
    public bool IsRunning => !_process.HasExited;
    public int DroppedWakeups => Volatile.Read(ref _droppedWakeups);

    /// <summary>The settings the worker resolved, reported before its readiness line; null for the oracle.</summary>
    public WorkerSettings? Settings { get; private set; }

    public void Pause() => WorkerPause.Set(_process, true);

    public void Resume() => WorkerPause.Set(_process, false);

    public async Task ReadyAsync(CancellationToken ct)
    {
        await _ready.Task.WaitAsync(ReadyTimeout, ct);
    }

    private async Task DrainAsync(StreamReader reader, bool readiness)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            _contractRejected |= line.Contains("PayloadContractDriftException", StringComparison.Ordinal);
            if (readiness && line.StartsWith("ACTA_BENCH_SETTINGS ", StringComparison.Ordinal))
            {
                var values = line.Split(' ')[1..].Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                Settings = new((int)values[0], values[1], (int)values[2], values[3], values[4]);
            }
            if (readiness && line == "ACTA_BENCH_READY")
            {
                _ready.TrySetResult();
            }
            if (readiness && line == "ACTA_BENCH_WAKEUP_DROPPED")
            {
                Interlocked.Increment(ref _droppedWakeups);
            }
            if (!readiness)
            {
                lock (_errorTail)
                {
                    _errorTail.Enqueue(line);
                    if (_errorTail.Count > 5)
                    {
                        _errorTail.Dequeue();
                    }
                }
            }
        }
        if (readiness)
        {
            // The worker writes why it stopped to stderr; that reason is the only evidence of a failed start.
            try
            {
                // Null only when stdout closed before the constructor started draining stderr.
                if (_error is { } error)
                {
                    await error.WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            catch (TimeoutException) { }
            string tail;
            lock (_errorTail)
            {
                tail = string.Join(" | ", _errorTail);
            }
            _ready.TrySetException(
                new InvalidOperationException(
                    tail.Length == 0 ? "Owned worker exited before readiness." : $"Owned worker exited before readiness: {tail}"
                )
            );
        }
    }

    public async Task<bool> ContractRejectedAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        await Task.WhenAll(_output, _error);
        return _process.ExitCode != 0 && _contractRejected;
    }

    public static OwnedWorker Start(
        OwnedProvider provider,
        EffectService? oracle = null,
        StoreInjection? injection = null,
        string? connectionOverride = null,
        bool suppressWakeup = false,
        string? outboxSource = null,
        string? fixture = null,
        int? executors = null,
        int? heartbeatSeconds = FaultHeartbeatSeconds,
        string? jobNamespace = null
    )
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate worker executable.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(
                Path.Combine(AppContext.BaseDirectory, System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name + ".dll")
            );
        }
        info.ArgumentList.Add("--worker");
        SetParent(info);
        if (jobNamespace is not null)
        {
            info.Environment["ACTA_BENCH_NAMESPACE"] = jobNamespace;
        }
        if (executors is not null)
        {
            info.Environment["ACTA_BENCH_EXECUTORS"] = executors.Value.ToString(CultureInfo.InvariantCulture);
        }
        if (heartbeatSeconds is not null)
        {
            info.Environment["ACTA_BENCH_HEARTBEAT_SECONDS"] = heartbeatSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }
        info.Environment["ACTA_BENCH_PROVIDER"] = provider.Provider;
        info.Environment["ACTA_BENCH_CONNECTION"] = connectionOverride ?? provider.ConnectionString;
        info.Environment["ACTA_BENCH_SCHEMA"] = provider.Schema;
        info.Environment["ACTA_BENCH_OWNER"] = provider.Ownership;
        info.Environment["ACTA_BENCH_SUPPRESS_WAKEUP"] = suppressWakeup ? "1" : "0";
        if (fixture is not null)
        {
            info.Environment["ACTA_BENCH_FIXTURE"] = fixture;
        }
        if (outboxSource is not null)
        {
            info.Environment["ACTA_BENCH_OUTBOX_SOURCE"] = outboxSource;
        }
        if (oracle is not null)
        {
            info.Environment["ACTA_BENCH_ORACLE"] = oracle.Endpoint;
            info.Environment["ACTA_BENCH_ORACLE_TOKEN"] = oracle.Token;
        }
        if (injection is not null)
        {
            info.Environment["ACTA_BENCH_INJECT_OPERATION"] = injection.Operation;
            info.Environment["ACTA_BENCH_INJECT_BOUNDARY"] = injection.Boundary;
            info.Environment["ACTA_BENCH_INJECT_LOSE_REPLY"] = injection.LoseReply ? "1" : "0";
            info.Environment["ACTA_BENCH_INJECT_JOB_ID"] =
                injection.JobId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start owned worker.");
        if (Affinity is { } mask && OperatingSystem.IsWindows())
        {
            process.ProcessorAffinity = (nint)mask;
        }
        return new OwnedWorker(process);
    }

    public static OwnedWorker Oracle(EffectService oracle)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate oracle executable.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(
                Path.Combine(AppContext.BaseDirectory, System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name + ".dll")
            );
        }
        info.ArgumentList.Add("--oracle");
        SetParent(info);
        info.Environment["ACTA_BENCH_ORACLE"] = oracle.Endpoint;
        info.Environment["ACTA_BENCH_ORACLE_TOKEN"] = oracle.Token;
        info.Environment["ACTA_BENCH_ORACLE_STORE"] = oracle.StorePath;
        return new(Process.Start(info) ?? throw new InvalidOperationException("Could not start owned oracle."));
    }

    public async Task<bool> StopAsync(CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync("ACTA_BENCH_STOP");
        await _process.StandardInput.FlushAsync(ct);
        await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
        await Task.WhenAll(_output, _error);
        return _process.ExitCode == 0;
    }

    public bool Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            return _process.WaitForExit(5000);
        }
        return true;
    }

    private static void SetParent(ProcessStartInfo info)
    {
        using var parent = Process.GetCurrentProcess();
        info.Environment["ACTA_BENCH_PARENT_PID"] = parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["ACTA_BENCH_PARENT_START"] = parent
            .StartTime.ToUniversalTime()
            .Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        Kill();
        // Worker diagnostics are deliberately not exported raw: driver failures can include secrets.
        _ = _output;
        _ = _error;
        _process.Dispose();
    }
}
