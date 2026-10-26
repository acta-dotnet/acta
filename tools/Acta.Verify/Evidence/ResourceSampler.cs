using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.Verify;

internal sealed record ResourceSample(
    long MonotonicTicks,
    string Component,
    string Name,
    double? Value,
    string Unit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool CounterReset = false
);

[JsonSerializable(typeof(ResourceSample))]
internal sealed partial class ResourceJson : JsonSerializerContext;

internal sealed class ResourceSampler : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _sampling;
    private readonly OwnedProvider _provider;
    private readonly EvidenceBundle _bundle;
    private readonly ConcurrentDictionary<int, (string Name, long StartedUtc)> _processes = [];
    private readonly Dictionary<string, double> _previous = new(StringComparer.Ordinal);
    private readonly object _summaryLock = new();
    private readonly Dictionary<string, (double First, double Last)> _cpu = new(StringComparer.Ordinal);
    private readonly HashSet<string> _explained = new(StringComparer.Ordinal);
    private long _peakWorkingSet;
    private double? _containerCpuFirst;
    private double? _containerCpuLast;
    private int _containerCpuSamples;
    private bool _containerResetObserved;
    private double? _containerPeakMemory;
    private double? _containerPeakWorkingMemory;
    private string _containerUnavailable = "No container samples were collected.";
    private long _nextContainerSample;
    private readonly string _tag;
    private readonly double _memoryBudget;
    private readonly long _freeDiskAtStart;
    private readonly DriveInfo _drive;
    public bool BudgetExceeded { get; private set; }

    public ResourceSampler(OwnedProvider provider, EvidenceBundle bundle, string tag, IReadOnlyList<int> workers, CancellationToken ct)
    {
        _provider = provider;
        _bundle = bundle;
        _tag = tag;
        foreach (var pid in workers)
        {
            AddProcess(pid, "worker");
        }
        AddProcess(Environment.ProcessId, "controller_generator_observer");
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _memoryBudget = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.65;
        _drive = new DriveInfo(Path.GetPathRoot(bundle.DirectoryPath)!);
        var environment = JsonSerializer.Deserialize(
            File.ReadAllText(bundle.Resolve("environment.json")),
            VerifyJson.Default.DictionaryStringString
        );
        _freeDiskAtStart =
            environment is not null
            && long.TryParse(
                environment.GetValueOrDefault("artifact_disk_free_at_start_bytes"),
                CultureInfo.InvariantCulture,
                out var runFree
            )
                ? runFree
                : _drive.AvailableFreeSpace;
        _sampling = SampleAsync();
    }

    public void AddProcess(int pid, string component)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            _processes[pid] = (component, process.StartTime.ToUniversalTime().Ticks);
        }
        catch (ArgumentException)
        {
            Write(component, "working_set", null, "bytes", "Owned process exited before its resource identity could be captured.");
        }
    }

    public void RemoveProcess(int pid) => _processes.TryRemove(pid, out _);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _sampling;
        }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    private async Task SampleAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            long memory = 0;
            Dictionary<string, (double Cpu, long Memory)> roles = new(StringComparer.Ordinal);
            foreach (var (pid, target) in _processes)
            {
                var component =
                    target.Name
                    + "-"
                    + pid.ToString(CultureInfo.InvariantCulture)
                    + "-"
                    + target.StartedUtc.ToString(CultureInfo.InvariantCulture);
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.StartTime.ToUniversalTime().Ticks != target.StartedUtc)
                    {
                        Write(
                            component,
                            "working_set",
                            null,
                            "bytes",
                            "PID was reused; the current process is not the recorded owned process."
                        );
                        continue;
                    }
                    var cpu = process.TotalProcessorTime.TotalSeconds;
                    memory += process.WorkingSet64;
                    lock (_summaryLock)
                    {
                        _cpu[component] = _cpu.TryGetValue(component, out var prior) ? (prior.First, cpu) : (cpu, cpu);
                    }
                    var role = roles.GetValueOrDefault(target.Name);
                    roles[target.Name] = (role.Cpu + cpu, role.Memory + process.WorkingSet64);
                }
                catch (ArgumentException)
                {
                    Write(component, "working_set", null, "bytes", "Owned process has exited.");
                }
                catch (InvalidOperationException)
                {
                    Write(component, "working_set", null, "bytes", "Owned process exited during collection.");
                }
            }
            // One sample per role, not per process, keeps the journal proportionate to the run rather than to the fleet.
            foreach (var (role, total) in roles)
            {
                Write(role, "cpu_seconds", total.Cpu, "seconds", "Sum over the role's live owned processes.");
                Write(role, "working_set", total.Memory, "bytes", "Sum over the role's live owned processes.");
            }
            UpdatePeakMemory(memory);
            Write("controller_generator_observer", "managed_allocated", GC.GetTotalAllocatedBytes(), "bytes");
            Write("controller_generator_observer", "gc_pause", GC.GetTotalPauseDuration().TotalSeconds, "seconds");
            var free = _drive.AvailableFreeSpace;
            BudgetExceeded |= memory > _memoryBudget || free < 2L * 1024 * 1024 * 1024 || _freeDiskAtStart - free > _freeDiskAtStart * 0.2;
            Write("host", "free_disk", free, "bytes");
            await DatabaseAsync(_stop.Token);
            if (Stopwatch.GetTimestamp() >= _nextContainerSample)
            {
                await ContainerAsync(_stop.Token);
                _nextContainerSample = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            }
            lock (_summaryLock)
            {
                // Docker caps the container, and the kernel reclaims its file cache inside that cap, so only the memory
                // it cannot reclaim competes with the host's processes.
                BudgetExceeded |= memory + (_containerPeakWorkingMemory ?? 0) > _memoryBudget;
            }
            await Task.Delay(1000, _stop.Token);
        }
    }

    public Dictionary<string, Metric> Summary(long completedRoots)
    {
        lock (_summaryLock)
        {
            var cpu = _cpu.Values.Sum(v => Math.Max(0, v.Last - v.First));
            return new(StringComparer.Ordinal)
            {
                ["database_process_memory"] = _containerPeakMemory is { } memory
                    ? new(
                        memory,
                        "bytes",
                        Reason: "Peak sampled database-container memory, including all databases and container processes on a reused server."
                    )
                    : Metric.Missing("bytes", _containerUnavailable),
                ["database_container_cpu"] =
                    _containerCpuSamples >= 2 && !_containerResetObserved
                        ? new(
                            _containerCpuLast - _containerCpuFirst,
                            "cpu_seconds",
                            Reason: "Sampled database-container CPU delta, including other databases on a reused server. Samples are five seconds apart; endpoints differ from worker CPU sampling."
                        )
                        : Metric.Missing(
                            "cpu_seconds",
                            _containerResetObserved
                                ? "Container CPU counter reset during this trial; a complete lifecycle delta is unavailable."
                                : "At least two container CPU samples are required. " + _containerUnavailable
                        ),
                ["database_waits"] = Metric.Missing("seconds", "Provider wait-event collector is unavailable."),
                ["database_live_storage"] = Metric.Missing(
                    "bytes",
                    "Live logical storage collector is unavailable; disk free space is not live data size."
                ),
                ["database_allocated_storage"] = Metric.Missing(
                    "bytes",
                    "Database allocation collector is unavailable; file growth is not bytes written."
                ),
                ["worker_gc_allocated"] = Metric.Missing(
                    "bytes",
                    "Worker runtime event collector is unavailable; controller GC metrics are separately attributed."
                ),
                ["worker_gc_pause"] = Metric.Missing("seconds", "Worker runtime event collector is unavailable."),
                ["physical_device_io"] = Metric.Missing(
                    "bytes",
                    "Physical-device I/O collector is unavailable; provider I/O counters are database-level only."
                ),
                ["sampled_owned_process_cpu"] =
                    cpu > 0
                        ? new(
                            cpu,
                            "cpu_seconds",
                            Reason: "Owned-process counter deltas between sampled endpoints, including covered warmup/drain time. Unobserved startup and database CPU are excluded."
                        )
                        : Metric.Missing(
                            "cpu_seconds",
                            "No positive CPU delta was observed at this sampling resolution; it is not a zero-cost claim."
                        ),
                ["sampled_owned_process_cpu_per_root"] =
                    completedRoots == 0 || cpu <= 0
                        ? Metric.Missing("cpu_seconds/root", "No reconciled successful roots or positive sampled CPU delta.")
                        : new(
                            cpu / completedRoots,
                            "cpu_seconds/root",
                            Reason: "Covered trial lifecycle cost, including sampled warmup/drain time. Unobserved process startup and database CPU are excluded; this is not total-system cost."
                        ),
                ["sampled_owned_process_peak_memory"] = new(
                    Interlocked.Read(ref _peakWorkingSet),
                    "bytes",
                    Reason: "Sum of sampled owned process working sets; database process memory is unavailable."
                ),
            };
        }
    }

    /// <summary>A <c>memory.stat</c> counter by name, or 0 when the kernel does not report it.</summary>
    private static double Stat(string[] lines, string name) =>
        lines.FirstOrDefault(l => l.StartsWith(name + " ", StringComparison.Ordinal)) is { } line
        && double.TryParse(line.AsSpan(name.Length + 1), CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private async Task ContainerAsync(CancellationToken ct)
    {
        if (_provider.ContainerId is null)
        {
            lock (_summaryLock)
            {
                _containerUnavailable = "Embedded SQLite database CPU and memory are included in owned worker processes.";
            }
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var process = new Process
            {
                StartInfo = new("docker")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (
                var argument in new[]
                {
                    "exec",
                    _provider.ContainerId,
                    "cat",
                    "/sys/fs/cgroup/cpu.stat",
                    "/sys/fs/cgroup/memory.stat",
                    "/sys/fs/cgroup/memory.current",
                }
            )
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            await error;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Container counters unavailable.");
            var lines = (await output).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var usage = lines.FirstOrDefault(l => l.StartsWith("usage_usec ", StringComparison.Ordinal));
            if (
                usage is null
                || !double.TryParse(usage.AsSpan("usage_usec ".Length), CultureInfo.InvariantCulture, out var microseconds)
                || !double.TryParse(lines.LastOrDefault(), CultureInfo.InvariantCulture, out var memory)
            )
            {
                throw new InvalidOperationException("Supported cgroup v2 counters are absent.");
            }
            // File cache is reclaimable except shared memory, which memory.stat counts inside "file": PostgreSQL's
            // shared_buffers live there and must stay counted.
            var working = memory - Math.Max(0, Stat(lines, "file") - Stat(lines, "shmem"));
            var cpu = microseconds / 1000000;
            bool reset;
            lock (_summaryLock)
            {
                reset = _containerCpuLast is { } previous && cpu < previous;
                if (reset)
                {
                    _containerResetObserved = true;
                    _containerCpuFirst = null;
                    _containerCpuSamples = 0;
                }
                _containerCpuFirst ??= cpu;
                _containerCpuLast = cpu;
                _containerCpuSamples++;
                _containerPeakMemory = Math.Max(_containerPeakMemory ?? 0, memory);
                _containerPeakWorkingMemory = Math.Max(_containerPeakWorkingMemory ?? 0, working);
                _containerUnavailable = "No complete trial-lifecycle interval is implied by sampled endpoints.";
            }
            Write(
                "database_container",
                "cpu_seconds",
                cpu,
                "seconds",
                "Whole-container counter, including other databases on a shared server.",
                reset
            );
            Write("database_container", "memory_current", memory, "bytes", "Whole-container memory, not per-database memory.");
            Write("database_container", "memory_working", working, "bytes", "Whole-container memory less reclaimable file cache.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            lock (_summaryLock)
            {
                _containerUnavailable = $"Container counter unavailable ({ex.GetType().Name}); it is not zero.";
            }
            Write("database_container", "cpu_seconds", null, "seconds", _containerUnavailable);
        }
    }

    private void UpdatePeakMemory(long value)
    {
        var prior = Interlocked.Read(ref _peakWorkingSet);
        while (value > prior)
        {
            var actual = Interlocked.CompareExchange(ref _peakWorkingSet, value, prior);
            if (actual == prior)
                return;
            prior = actual;
        }
    }

    private async Task DatabaseAsync(CancellationToken ct)
    {
        if (_provider.Provider == "sqlite")
        {
            Write("database", "io_bytes", null, "bytes", "SQLite runs inside workers; process I/O is not a physical-device counter.");
            return;
        }
        try
        {
            await using var connection = await _provider.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText =
                _provider.Provider == "pg"
                    ? "SELECT wal_bytes FROM pg_stat_wal;"
                    : "SELECT SUM(num_of_bytes_read), SUM(num_of_bytes_written) FROM sys.dm_io_virtual_file_stats(DB_ID(), NULL);";
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                Counter(
                    "database",
                    _provider.Provider == "pg" ? "wal_bytes" : "file_read_bytes",
                    Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture)
                );
                if (reader.FieldCount > 1)
                {
                    Counter("database", "file_write_bytes", Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Write("database", "io_bytes", null, "bytes", $"Counter unavailable ({ex.GetType().Name}); never interpreted as zero.");
        }
    }

    private void Counter(string component, string name, double value)
    {
        var reset = _previous.TryGetValue(name, out var previous) && value < previous;
        _previous[name] = value;
        Write(
            component,
            name,
            value,
            "bytes",
            _provider.Provider == "pg"
                ? "PostgreSQL cluster-wide WAL counter; includes other databases on a reused server. Not physical-device I/O."
                : "Current-database file-I/O counter; not physical-device I/O.",
            reset
        );
    }

    private void Write(string component, string name, double? value, string unit, string? reason = null, bool reset = false)
    {
        // A series' reason does not change, so only its first sample carries it; a missing value always explains itself.
        if (value is not null && reason is not null && !_explained.Add(component + "/" + name))
        {
            reason = null;
        }
        var sample = new ResourceSample(Stopwatch.GetTimestamp(), $"{_tag}/{component}", name, value, unit, reason, reset);
        File.AppendAllText(_bundle.Resolve("metrics.jsonl"), JsonSerializer.Serialize(sample, ResourceJson.Default.ResourceSample) + "\n");
    }
}
