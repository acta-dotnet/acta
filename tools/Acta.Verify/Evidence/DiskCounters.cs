using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Acta.Verify;

/// <summary>
/// Windows disk counters for the whole run, read by one Get-Counter process every two seconds, so the harness writes
/// nothing to measure the drive. Each sample is filed under the phase the run is in: before the first provider, each
/// provider, and after the last. A QLC drive that folded its write cache after sustained writes shows it as write
/// latency several times the fresh figure, and server trials measured in that state read about half.
/// </summary>
public sealed class DiskCounters : IDisposable
{
    // Runs while the run's process lives, so a killed run leaves no sampler behind.
    private const string Script =
        "[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture; "
        + "$c = '\\PhysicalDisk(*)\\Avg. Disk sec/Write', '\\PhysicalDisk(*)\\Disk Writes/sec', '\\PhysicalDisk(*)\\% Idle Time'; "
        + "while (Get-Process -Id $parent -ErrorAction SilentlyContinue) { $r = Get-Counter $c -SampleInterval 2 -MaxSamples 1 -ErrorAction SilentlyContinue; if ($null -eq $r) { Start-Sleep -Seconds 2; continue }; "
        + "foreach ($g in ($r.CounterSamples | Where-Object { $_.InstanceName -ne '_total' } | Group-Object InstanceName)) { "
        + "$v = @{}; foreach ($s in $g.Group) { $v[($s.Path -split '\\\\')[-1]] = $s.CookedValue }; "
        + "[Console]::Out.WriteLine(('{0}|{1}|{2}|{3}' -f $g.Name, ($v['avg. disk sec/write'] * 1000), $v['disk writes/sec'], $v['% idle time'])); "
        + "[Console]::Out.Flush() } }";

    private readonly Process? _process;
    private readonly List<(string Phase, Sample Sample)> _samples = [];
    private readonly Queue<(long Ticks, Sample Sample)> _recent = [];

    // Null between a save and the next phase, so a late sample cannot reopen a phase already saved.
    private volatile string? _phase = "before";

    private DiskCounters(Process? process) => _process = process;

    /// <summary>Starts sampling, or records nothing where Windows counters or PowerShell 7 are unavailable.</summary>
    public static DiskCounters Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(null);
        }
        try
        {
            var process = new Process
            {
                StartInfo = new("pwsh")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            var command = string.Create(CultureInfo.InvariantCulture, $"$parent = {Environment.ProcessId}; ") + Script;
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            var counters = new DiskCounters(process);
            process.OutputDataReceived += (_, line) => counters.Add(line.Data);
            process.ErrorDataReceived += (_, _) => { };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return counters;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(null);
        }
    }

    /// <summary>Files the samples that follow under <paramref name="phase"/>.</summary>
    public void Enter(string phase) => _phase = phase;

    /// <summary>Stops sampling and saves what is left.</summary>
    public void Stop(EvidenceBundle bundle)
    {
        Dispose();
        Save(bundle);
    }

    /// <summary>
    /// Merges the samples taken so far into the run's <c>histories/host-disk.json</c> and starts afresh, so a provider's
    /// figures are saved before its finished marker and a crash later in the run cannot lose them.
    /// </summary>
    public void Save(EvidenceBundle bundle)
    {
        List<(string Phase, Sample Sample)> samples;
        lock (_samples)
        {
            samples = [.. _samples];
            _samples.Clear();
            _phase = null;
        }
        var path = bundle.Resolve("histories/host-disk.json");
        var merged = File.Exists(path)
            ? JsonSerializer.Deserialize(File.ReadAllText(path), VerifyJson.Default.DictionaryStringString) ?? []
            : [];
        foreach (var (key, value) in Summarize(samples))
        {
            merged[key] = value;
        }
        if (merged.Count > 0)
        {
            bundle.Write("histories/host-disk.json", merged, VerifyJson.Default.DictionaryStringString);
        }
    }

    /// <summary>
    /// One entry per phase and drive, <c>"pg | 1 D:"</c>, holding the mean and p95 write latency over the samples that
    /// wrote, the mean writes per second, and the mean idle percentage.
    /// </summary>
    public static Dictionary<string, string> Summarize(IEnumerable<(string Phase, Sample Sample)> samples) =>
        samples
            .GroupBy(s => $"{s.Phase} | {s.Sample.Drive}")
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latency = g.Where(s => s.Sample.WritesPerSecond > 0).Select(s => s.Sample.WriteMs).Order().ToArray();
                    var mean = latency.Length == 0 ? "-" : latency.Average().ToString("F2", CultureInfo.InvariantCulture);
                    var p95 =
                        latency.Length == 0
                            ? "-"
                            : latency[Math.Min(latency.Length - 1, (int)Math.Ceiling(0.95 * latency.Length) - 1)]
                                .ToString("F2", CultureInfo.InvariantCulture);
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"write {mean} ms mean, {p95} ms p95, {g.Average(s => s.Sample.WritesPerSecond):F0} writes/s, {g.Average(s => s.Sample.IdlePercent):F0}% idle, {g.Count()} samples"
                    );
                },
                StringComparer.Ordinal
            );

    /// <summary>Parses one <c>drive|write ms|writes per second|idle percent</c> line; null for anything else.</summary>
    public static Sample? Parse(string? line)
    {
        var parts = line?.Split('|');
        return
            parts is [var drive, var ms, var writes, var idle]
            && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var writeMs)
            && double.TryParse(writes, NumberStyles.Float, CultureInfo.InvariantCulture, out var writesPerSecond)
            && double.TryParse(idle, NumberStyles.Float, CultureInfo.InvariantCulture, out var idlePercent)
            ? new(drive, writeMs, writesPerSecond, idlePercent)
            : null;
    }

    public void Dispose()
    {
        if (_process is null)
        {
            return;
        }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }

    /// <summary>
    /// Waits until every drive has been quiet for <paramref name="quiet"/>: idle at least <paramref name="minIdlePercent"/>
    /// and writing at most <paramref name="maxWritesPerSecond"/> in every sample. It waits at most
    /// <paramref name="limit"/> and never refuses: a run that starts without its quiet minute says so, with the
    /// processes writing most, since a writer the run cannot see, such as a probe inside a container, still shows here.
    /// </summary>
    public async Task<string> WaitForQuietAsync(
        TimeSpan quiet,
        TimeSpan limit,
        double minIdlePercent,
        double maxWritesPerSecond,
        CancellationToken ct
    )
    {
        if (_process is null)
        {
            return "unknown: Windows disk counters are unavailable";
        }
        var waited = Stopwatch.StartNew();
        while (true)
        {
            (long Ticks, Sample Sample)[] window;
            lock (_samples)
            {
                var since = Stopwatch.GetTimestamp() - (long)(quiet.TotalSeconds * Stopwatch.Frequency);
                window = [.. _recent.Where(r => r.Ticks >= since)];
            }
            var covered = window.Length > 0 && Stopwatch.GetElapsedTime(window.Min(r => r.Ticks)) >= quiet * 0.9;
            var loud = window.Where(r => r.Sample.IdlePercent < minIdlePercent || r.Sample.WritesPerSecond > maxWritesPerSecond).ToArray();
            if (covered && loud.Length == 0)
            {
                return string.Create(CultureInfo.InvariantCulture, $"yes, after {waited.Elapsed.TotalSeconds:F0} s");
            }
            if (waited.Elapsed >= limit)
            {
                var worst = loud.Length == 0 ? "too few samples" : Describe(loud.MaxBy(r => r.Sample.WritesPerSecond).Sample);
                var writers = await TopWritersAsync(ct);
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"no, after {waited.Elapsed.TotalSeconds:F0} s: {worst}; writing most: {writers}"
                );
            }
            await Task.Delay(2000, ct);
        }

        static string Describe(Sample s) =>
            string.Create(CultureInfo.InvariantCulture, $"{s.Drive} {s.WritesPerSecond:F0} writes/s, {s.IdlePercent:F0}% idle");
    }

    /// <summary>The three processes writing the most bytes right now, by the per-process Windows counter.</summary>
    private static async Task<string> TopWritersAsync(CancellationToken ct)
    {
        try
        {
            var output = await OwnedProvider.RunAsync(
                "pwsh",
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture; "
                        + "(Get-Counter '\\Process(*)\\IO Write Bytes/sec' -ErrorAction SilentlyContinue).CounterSamples "
                        + "| Where-Object { $_.InstanceName -notin '_total', 'idle' } | Sort-Object CookedValue -Descending "
                        + "| Select-Object -First 3 | ForEach-Object { '{0} {1:F1} MB/s' -f $_.InstanceName, ($_.CookedValue / 1MB) }",
                ],
                null,
                ct
            );
            return string.Join(", ", output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "unknown";
        }
    }

    private void Add(string? line)
    {
        if (Parse(line) is { } sample)
        {
            lock (_samples)
            {
                if (_phase is { } phase)
                {
                    _samples.Add((phase, sample));
                }
                _recent.Enqueue((Stopwatch.GetTimestamp(), sample));
                // Five minutes of samples for every drive is more than any quiet window asks for.
                while (_recent.Count > 600)
                {
                    _recent.Dequeue();
                }
            }
        }
    }

    /// <summary>One drive's counters at one sample.</summary>
    public sealed record Sample(string Drive, double WriteMs, double WritesPerSecond, double IdlePercent);
}
