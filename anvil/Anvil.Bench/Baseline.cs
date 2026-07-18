using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Acta;
using Acta.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Anvil.Bench;

public sealed record BaselinePolicy(int WarmupIterations, int MeasuredRepeats, string Aggregation);

public sealed record BenchPreset(string Name, BaselinePolicy Policy, int Jobs, int QueryRows, bool FullMatrix);

public sealed record BaselineEnvironmentInfo(
    string DotnetVersion,
    string Os,
    string CpuModel,
    int LogicalProcessorCount,
    long TotalMemoryBytes,
    string Disk
);

public sealed record BaselineDatabaseInfo(
    string Provider,
    string DatabaseServerVersion,
    string DatabaseEngineMajorVersion,
    string DatabaseProviderVersion,
    string DatabaseLocation,
    string ConnectionStringFingerprint
);

public sealed record BaselineFile(
    int SchemaVersion,
    string Preset,
    string CapturedAtUtc,
    string EngineVersion,
    string GitCommit,
    bool GitDirty,
    BaselinePolicy Policy,
    BaselineEnvironmentInfo Environment,
    IReadOnlyList<BaselineDatabaseInfo> Databases,
    IReadOnlyList<BaselineCellResult> Cells
);

public sealed record BaselineCellKey(
    string Scenario,
    string Provider,
    string DatabaseEngineMajorVersion,
    string? ExecutionProfile,
    int Jobs,
    int Executors,
    int ClaimBatch,
    int PayloadBytes,
    int Workers,
    int Rows,
    int Iterations
);

public sealed record BaselineMetrics(
    double JobsPerSecond,
    double EnqueuePerSecond,
    double DrainPerSecond,
    double DurationMs,
    double P50LatencyMs,
    double P95LatencyMs,
    double P99LatencyMs,
    double MaxLatencyMs,
    double MeanLatencyMs,
    double EnqueueDurationMs,
    double DrainDurationMs,
    int JobsObserved,
    long AllocatedBytes,
    int GcCollections,
    IReadOnlyDictionary<string, double>? ExtraMetrics
);

public sealed record BaselineCellResult(
    BaselineCellKey Key,
    string Status,
    BaselineMetrics MedianMetrics,
    IReadOnlyList<BaselineMetrics> Repeats,
    IReadOnlyList<string> RepeatStatuses
);

public sealed record BaselineCellSpec(
    string Scenario,
    string ActualScenario,
    string Provider,
    ExecutionProfile? KeyProfile,
    CellParams ActualParams,
    BaselineCellKey Key
);

public delegate Task<CellResult> BaselineCellRunner(BaselineCellSpec spec, int repeatIndex, bool warmup, CancellationToken ct);

/// <summary>
/// The committed baseline suite. Names describe what is measured; sizes, profiles, and prefetch live in
/// the cell parameters so the JSON remains explicit.
/// </summary>
public static class BaselineSuite
{
    public static readonly BenchPreset QuickPreset = new("quick", new BaselinePolicy(0, 1, "single"), 1_000, 10_000, FullMatrix: false);
    public static readonly BenchPreset FullPreset = new("full", new BaselinePolicy(1, 3, "median"), 10_000, 100_000, FullMatrix: true);

    private static readonly string[] Providers = ["sqlite", "pg", "mssql"];
    private static readonly ExecutionProfile[] ExecutionProfiles =
    [
        ExecutionProfile.Buffered,
        ExecutionProfile.Direct,
        ExecutionProfile.Bulk,
    ];

    public static BenchPreset Preset(string name)
    {
        if (string.Equals(name, QuickPreset.Name, StringComparison.OrdinalIgnoreCase))
        {
            return QuickPreset;
        }
        if (string.Equals(name, FullPreset.Name, StringComparison.OrdinalIgnoreCase))
        {
            return FullPreset;
        }

        throw new ArgumentException($"Unknown preset '{name}' (expected quick|full).");
    }

    public static IReadOnlyList<BaselineCellSpec> Cells(
        BenchPreset preset,
        IReadOnlyDictionary<string, BaselineDatabaseInfo>? databases = null,
        IReadOnlyList<string>? providers = null,
        IReadOnlyList<string>? scenarios = null
    )
    {
        var specs = new List<BaselineCellSpec>();
        var profiles = preset.FullMatrix ? ExecutionProfiles : new[] { ExecutionProfile.Direct };
        var throughputExecutors = preset.FullMatrix ? new[] { 1, 2, 4, 8, 16, 32 } : new[] { 1, 8, 32 };
        var producerCounts = preset.FullMatrix ? new[] { 1, 4, 16 } : new[] { 1, 16 };

        foreach (var provider in NormalizeProviders(providers))
        {
            var dbVersion =
                databases is not null && databases.TryGetValue(provider, out var db) ? db.DatabaseEngineMajorVersion : "unknown";

            foreach (var profile in profiles)
            foreach (var executors in throughputExecutors)
            {
                Add(
                    specs,
                    scenario: "throughput",
                    actualScenario: "throughput",
                    provider,
                    dbVersion,
                    keyProfile: profile,
                    actualProfile: profile,
                    jobs: preset.Jobs,
                    executors,
                    claimBatch: executors * 2,
                    payloadBytes: 0,
                    workers: 1,
                    rows: 0,
                    iterations: 200
                );
            }

            foreach (var profile in profiles)
            {
                Add(
                    specs,
                    scenario: "latency",
                    actualScenario: "latency",
                    provider,
                    dbVersion,
                    keyProfile: profile,
                    actualProfile: profile,
                    jobs: 0,
                    executors: 1,
                    claimBatch: 2,
                    payloadBytes: 0,
                    workers: 1,
                    rows: 0,
                    iterations: 200
                );
            }

            // SQLite is single-writer, so multi-worker drain just re-measures that it does not scale.
            var drainWorkers =
                provider == "sqlite" ? new[] { 1 }
                : preset.FullMatrix ? new[] { 1, 4, 16 }
                : new[] { 1, 16 };
            foreach (var profile in profiles)
            foreach (var workers in drainWorkers)
            {
                Add(
                    specs,
                    scenario: "drain",
                    actualScenario: "drain",
                    provider,
                    dbVersion,
                    keyProfile: profile,
                    actualProfile: profile,
                    jobs: preset.Jobs,
                    executors: 16,
                    claimBatch: 32,
                    payloadBytes: 0,
                    workers,
                    rows: 0,
                    iterations: 200
                );
            }

            foreach (var producers in producerCounts)
            {
                Add(
                    specs,
                    scenario: "enqueue",
                    actualScenario: "enqueue",
                    provider,
                    dbVersion,
                    keyProfile: null,
                    actualProfile: ExecutionProfile.Direct,
                    jobs: preset.Jobs,
                    executors: 0,
                    claimBatch: 0,
                    payloadBytes: 0,
                    workers: producers,
                    rows: 0,
                    iterations: 200,
                    actualExecutors: 1,
                    actualClaimBatch: 2
                );
            }

            foreach (var producers in producerCounts)
            {
                Add(
                    specs,
                    scenario: "enqueue-batch",
                    actualScenario: "enqueue-batch",
                    provider,
                    dbVersion,
                    keyProfile: null,
                    actualProfile: ExecutionProfile.Direct,
                    jobs: preset.Jobs,
                    executors: 0,
                    claimBatch: 0,
                    payloadBytes: 0,
                    workers: producers,
                    rows: 0,
                    iterations: 200,
                    actualExecutors: 1,
                    actualClaimBatch: 2
                );
            }

            Add(
                specs,
                scenario: "query-list",
                actualScenario: "query",
                provider,
                dbVersion,
                keyProfile: null,
                actualProfile: ExecutionProfile.Direct,
                jobs: 0,
                executors: 0,
                claimBatch: 0,
                payloadBytes: 0,
                workers: 0,
                rows: preset.QueryRows,
                iterations: 200,
                actualExecutors: 1,
                actualClaimBatch: 2
            );
        }

        return scenarios is null ? specs : specs.Where(s => scenarios.Contains(s.Scenario, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    public static IReadOnlyList<string> NormalizeProviders(IReadOnlyList<string>? providers)
    {
        if (providers is null || providers.Count == 0)
        {
            return Providers;
        }

        var normalized = new List<string>();
        foreach (var provider in providers)
        {
            var value = provider.Trim().ToLowerInvariant() switch
            {
                "sqlite" or "sqlite3" => "sqlite",
                "pg" or "postgres" or "postgresql" => "pg",
                "mssql" or "sqlserver" or "sql-server" => "mssql",
                _ => throw new ArgumentException($"Unknown baseline provider '{provider}' (expected sqlite|pg|mssql)."),
            };

            if (!normalized.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private static void Add(
        List<BaselineCellSpec> specs,
        string scenario,
        string actualScenario,
        string provider,
        string dbVersion,
        ExecutionProfile? keyProfile,
        ExecutionProfile actualProfile,
        int jobs,
        int executors,
        int claimBatch,
        int payloadBytes,
        int workers,
        int rows,
        int iterations,
        int? actualExecutors = null,
        int? actualClaimBatch = null
    )
    {
        var key = new BaselineCellKey(
            scenario,
            provider,
            dbVersion,
            keyProfile?.ToString(),
            jobs,
            executors,
            claimBatch,
            payloadBytes,
            workers,
            rows,
            iterations
        );
        var actual = new CellParams(
            provider,
            jobs,
            actualExecutors ?? Math.Max(1, executors),
            actualClaimBatch ?? Math.Max(1, claimBatch),
            payloadBytes,
            iterations,
            Math.Max(1, workers),
            rows,
            actualProfile
        );
        specs.Add(new BaselineCellSpec(scenario, actualScenario, provider, keyProfile, actual, key));
    }
}

public static class BaselineCapture
{
    public const int SchemaVersion = 2;

    public static async Task<BaselineFile> CaptureAsync(
        BenchPreset preset,
        CancellationToken ct,
        BaselineCellRunner? runner = null,
        IReadOnlyList<BaselineDatabaseInfo>? databaseInfo = null,
        IReadOnlyList<string>? providers = null,
        IReadOnlyList<string>? scenarios = null
    )
    {
        var normalizedProviders = BaselineSuite.NormalizeProviders(providers);
        var databases = databaseInfo ?? await BaselineEnvironment.CaptureDatabasesAsync(ct, normalizedProviders).ConfigureAwait(false);
        var byProvider = databases.ToDictionary(d => d.Provider, StringComparer.OrdinalIgnoreCase);
        var specs = BaselineSuite.Cells(preset, byProvider, normalizedProviders, scenarios);
        runner ??= (spec, repeatIndex, warmup, runCt) => RunCellAsync(spec, repeatIndex, warmup, runCt);

        var results = new List<BaselineCellResult>(specs.Count);
        foreach (var spec in specs)
        {
            for (var warmup = 0; warmup < preset.Policy.WarmupIterations; warmup++)
            {
                await runner(spec, warmup, warmup: true, ct).ConfigureAwait(false);
            }

            var repeats = new List<BaselineMetrics>(preset.Policy.MeasuredRepeats);
            var statuses = new List<string>(preset.Policy.MeasuredRepeats);
            for (var repeat = 0; repeat < preset.Policy.MeasuredRepeats; repeat++)
            {
                var result = await runner(spec, repeat, warmup: false, ct).ConfigureAwait(false);
                repeats.Add(BaselineMetricMapper.FromCell(result.Metrics));
                statuses.Add(result.Status);
            }

            results.Add(new BaselineCellResult(spec.Key, AggregateStatus(statuses), BaselineAggregator.Median(repeats), repeats, statuses));
        }

        return new BaselineFile(
            SchemaVersion,
            preset.Name,
            DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            BaselineEnvironment.EngineVersion(),
            BaselineEnvironment.GitCommit(),
            BaselineEnvironment.GitDirty(),
            preset.Policy,
            BaselineEnvironment.CaptureHost(),
            databases,
            results
        );
    }

    public static async Task<CellResult> RunCellAsync(
        BaselineCellSpec spec,
        int repeatIndex,
        bool warmup,
        CancellationToken ct,
        BenchConfig? config = null
    )
    {
        var scenario =
            ScenarioRegistry.Find(spec.ActualScenario)
            ?? throw new ArgumentException($"Unknown baseline scenario '{spec.ActualScenario}'.", nameof(spec));
        var schema = BenchIdentity.NewSchema(DateTime.UtcNow, $"{repeatIndex:x2}{(warmup ? "w" : "m")}");
        try
        {
            var locksBefore = await ProviderConn.TryReadLockStatsAsync(spec.Provider, schema, ct).ConfigureAwait(false);
            var metrics = await scenario.RunAsync(spec.ActualParams, schema, config ?? new BenchConfig(), ct).ConfigureAwait(false);
            metrics = await WithLockStatsAsync(metrics, spec.Provider, schema, locksBefore, ct).ConfigureAwait(false);
            var status = metrics.JobsObserved >= scenario.ExpectedObserved(spec.ActualParams) ? "ok" : "incomplete";
            return new CellResult(spec.Scenario, spec.ActualParams, metrics, status, null);
        }
        catch (BenchDbUnavailableException ex)
        {
            return new CellResult(spec.Scenario, spec.ActualParams, Zero(), "skipped:db-unavailable", ex.Message);
        }
    }

    /// <summary>
    /// Attaches the cell's server-side locking cost as extra metrics: <c>deadlocks</c> (pg and
    /// mssql) plus <c>lockWaits</c> / <c>lockWaitMs</c> (mssql only) as before/after counter deltas.
    /// The healthy value is zero deadlocks on every cell; a nonzero delta after a refactoring is the
    /// deterministic "we introduced lock contention" signal recorded in the baseline output.
    /// </summary>
    private static async Task<CellMetrics> WithLockStatsAsync(
        CellMetrics metrics,
        string provider,
        string schema,
        BenchLockStats? before,
        CancellationToken ct
    )
    {
        if (before is null)
        {
            return metrics;
        }

        var after = await ProviderConn.TryReadLockStatsAsync(provider, schema, ct).ConfigureAwait(false);
        if (after is null)
        {
            return metrics;
        }

        var extra = metrics.Extra is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : new Dictionary<string, double>(metrics.Extra, StringComparer.Ordinal);
        extra["deadlocks"] = after.Deadlocks - before.Deadlocks;
        if (before.LockWaits is { } waits0 && after.LockWaits is { } waits1)
        {
            extra["lockWaits"] = waits1 - waits0;
        }
        if (before.LockWaitMs is { } waitMs0 && after.LockWaitMs is { } waitMs1)
        {
            extra["lockWaitMs"] = waitMs1 - waitMs0;
        }
        if (before.PageLatchWaitMs is { } latch0 && after.PageLatchWaitMs is { } latch1)
        {
            extra["pageLatchWaitMs"] = latch1 - latch0;
        }
        if (before.WriteLogWaitMs is { } log0 && after.WriteLogWaitMs is { } log1)
        {
            extra["writeLogWaitMs"] = log1 - log0;
        }
        return metrics with { Extra = extra };
    }

    public static void Write(BaselineFile baseline, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var json = JsonSerializer.Serialize(baseline, BaselineJsonContext.Default.BaselineFile);
        File.WriteAllText(path, json);
    }

    public static string OutputPath(BaselineFile baseline, string? outPath)
    {
        if (string.IsNullOrWhiteSpace(outPath))
        {
            outPath = OutputDirectory();
        }

        return string.Equals(Path.GetExtension(outPath), ".json", StringComparison.OrdinalIgnoreCase)
            ? outPath
            : Path.Combine(outPath, $"baseline-{Stamp(baseline.CapturedAtUtc)}.json");
    }

    /// <summary>Repo root (nearest ancestor holding <c>Acta.slnx</c>), or the current directory if none.</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Acta.slnx")))
            {
                return dir.FullName;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    /// <summary>A repo-relative directory resolved to an absolute path.</summary>
    public static string RepoSubdir(string relative) => Path.GetFullPath(Path.Combine(RepoRoot(), relative));

    public static string OutputDirectory() => RepoSubdir(Path.Combine("anvil", "Anvil.Bench", ".benchmarks"));

    public static BaselineFile Read(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), BaselineJsonContext.Default.BaselineFile)
        ?? throw new InvalidOperationException($"Could not parse baseline file: {path}");

    private static string AggregateStatus(IReadOnlyList<string> statuses)
    {
        if (statuses.All(s => string.Equals(s, "ok", StringComparison.Ordinal)))
        {
            return "ok";
        }

        if (statuses.Any(s => s.StartsWith("skipped:", StringComparison.Ordinal)))
        {
            return statuses.First(s => s.StartsWith("skipped:", StringComparison.Ordinal));
        }

        return "incomplete";
    }

    private static CellMetrics Zero() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static string Stamp(string capturedAtUtc) =>
        DateTime.TryParse(
            capturedAtUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var utc
        )
            ? utc.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture)
            : DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
}

public static class BaselineAggregator
{
    public static BaselineMetrics Median(IReadOnlyList<BaselineMetrics> metrics)
    {
        if (metrics.Count == 0)
        {
            return new BaselineMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
        }

        var extraKeys = metrics
            .SelectMany(m => m.ExtraMetrics?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        var extra =
            extraKeys.Length == 0
                ? null
                : extraKeys.ToDictionary(
                    k => k,
                    k =>
                        Median(
                            metrics.Select(m => m.ExtraMetrics is not null && m.ExtraMetrics.TryGetValue(k, out var v) ? v : 0).ToArray()
                        ),
                    StringComparer.Ordinal
                );

        return new BaselineMetrics(
            Median(metrics.Select(m => m.JobsPerSecond).ToArray()),
            Median(metrics.Select(m => m.EnqueuePerSecond).ToArray()),
            Median(metrics.Select(m => m.DrainPerSecond).ToArray()),
            Median(metrics.Select(m => m.DurationMs).ToArray()),
            Median(metrics.Select(m => m.P50LatencyMs).ToArray()),
            Median(metrics.Select(m => m.P95LatencyMs).ToArray()),
            Median(metrics.Select(m => m.P99LatencyMs).ToArray()),
            Median(metrics.Select(m => m.MaxLatencyMs).ToArray()),
            Median(metrics.Select(m => m.MeanLatencyMs).ToArray()),
            Median(metrics.Select(m => m.EnqueueDurationMs).ToArray()),
            Median(metrics.Select(m => m.DrainDurationMs).ToArray()),
            (int)Math.Round(Median(metrics.Select(m => (double)m.JobsObserved).ToArray())),
            (long)Math.Round(Median(metrics.Select(m => (double)m.AllocatedBytes).ToArray())),
            (int)Math.Round(Median(metrics.Select(m => (double)m.GcCollections).ToArray())),
            extra
        );
    }

    public static double Median(double[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        Array.Sort(values);
        var mid = values.Length / 2;
        return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }
}

public static class BaselineMetricMapper
{
    public static BaselineMetrics FromCell(CellMetrics m) =>
        new(
            JobsPerSecond: m.EndToEndRatePerSec,
            EnqueuePerSecond: m.EnqueueRatePerSec,
            DrainPerSecond: m.DrainRatePerSec,
            DurationMs: Math.Max(m.DrainSeconds, m.EnqueueSeconds) * 1000.0,
            P50LatencyMs: m.LatencyP50Ms,
            P95LatencyMs: m.LatencyP95Ms,
            P99LatencyMs: m.LatencyP99Ms,
            MaxLatencyMs: m.LatencyMaxMs,
            MeanLatencyMs: m.LatencyMeanMs,
            EnqueueDurationMs: m.EnqueueSeconds * 1000.0,
            DrainDurationMs: m.DrainSeconds * 1000.0,
            JobsObserved: m.JobsObserved,
            AllocatedBytes: 0,
            GcCollections: 0,
            ExtraMetrics: m.Extra
        );
}

public static class BaselineReport
{
    public static void WriteMarkdown(BaselineFile baseline, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, Markdown(baseline));
    }

    public static string Markdown(BaselineFile baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Benchmark Baseline: {baseline.Preset}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Captured: {baseline.CapturedAtUtc}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Commit: {baseline.GitCommit}{(baseline.GitDirty ? " (dirty)" : "")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Engine: {baseline.EngineVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Runtime: {baseline.Environment.DotnetVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- OS: {baseline.Environment.Os}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"- CPU: {baseline.Environment.CpuModel} ({baseline.Environment.LogicalProcessorCount} logical)"
        );
        sb.AppendLine(CultureInfo.InvariantCulture, $"- RAM: {BaselineEnvironment.FormatBytes(baseline.Environment.TotalMemoryBytes)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Disk: {baseline.Environment.Disk}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Policy: {PolicyText(baseline.Policy)}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"- Providers: {string.Join(", ", baseline.Cells.Select(c => c.Key.Provider).Distinct().Order())}"
        );
        sb.AppendLine();

        AppendThroughput(sb, baseline.Cells);
        AppendDrain(sb, baseline.Cells);
        AppendLatency(sb, baseline.Cells);
        AppendEnqueue(sb, baseline.Cells);
        AppendEnqueueBatch(sb, baseline.Cells);
        AppendQuery(sb, baseline.Cells);
        return sb.ToString();
    }

    // Production-default first, then combined-durable, then relaxed - so a reader scans profiles in
    // increasing-speed / decreasing-durability order rather than alphabetically.
    private static readonly string[] ProfileRank = ["Buffered", "Direct", "Bulk"];

    private static int ProfileOrder(string? profile)
    {
        var i = Array.IndexOf(ProfileRank, profile);
        return i >= 0 ? i : ProfileRank.Length;
    }

    private static void AppendThroughput(StringBuilder sb, IReadOnlyList<BaselineCellResult> cells)
    {
        var rows = cells.Where(c => c.Key.Scenario == "throughput").ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        sb.AppendLine("## Throughput: end-to-end jobs/s (higher is better)");
        sb.AppendLine();
        sb.AppendLine("One worker process; executors swept per worker. Cells are median jobs/s.");
        sb.AppendLine();
        PivotByProfile(
            sb,
            rows,
            k => k.Executors,
            e => $"e={e}",
            m => m.JobsPerSecond,
            "jobs/s",
            k => $"{k.ExecutionProfile}, {k.Executors} executors"
        );
    }

    private static void AppendDrain(StringBuilder sb, IReadOnlyList<BaselineCellResult> cells)
    {
        var rows = cells.Where(c => c.Key.Scenario == "drain").ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        sb.AppendLine("## Drain: sustained jobs/s (higher is better)");
        sb.AppendLine();
        sb.AppendLine("A preloaded backlog drained by N worker processes, 16 executors each. Cells are median jobs/s.");
        sb.AppendLine();
        PivotByProfile(
            sb,
            rows,
            k => k.Workers,
            w => $"w={w}",
            m => m.DrainPerSecond,
            "jobs/s",
            k => $"{k.ExecutionProfile}, {k.Workers} worker{(k.Workers == 1 ? "" : "s")}"
        );
    }

    // Profiles as rows, one swept dimension (executors or workers) as columns, one metric per cell, plus a
    // peak callout - a compact matrix that scans far better than one row per (profile, dimension) tuple.
    // Groups by provider and only prints a provider subheading when more than one provider is present.
    private static void PivotByProfile(
        StringBuilder sb,
        IReadOnlyList<BaselineCellResult> rows,
        Func<BaselineCellKey, int> column,
        Func<int, string> columnHeader,
        Func<BaselineMetrics, double> value,
        string unit,
        Func<BaselineCellKey, string> peakWhere
    )
    {
        var multiProvider = rows.Select(r => r.Key.Provider).Distinct().Count() > 1;
        foreach (var group in rows.GroupBy(r => r.Key.Provider).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (multiProvider)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"### {group.Key}");
                sb.AppendLine();
            }

            var columns = group.Select(r => column(r.Key)).Distinct().OrderBy(x => x).ToArray();
            var profiles = group.Select(r => r.Key.ExecutionProfile).Distinct().OrderBy(ProfileOrder).ToArray();
            var byCell = group.ToDictionary(r => (r.Key.ExecutionProfile, column(r.Key)));

            sb.Append("| profile |");
            foreach (var c in columns)
            {
                sb.Append(CultureInfo.InvariantCulture, $" {columnHeader(c)} |");
            }
            sb.AppendLine();
            sb.Append("| --- |");
            foreach (var _ in columns)
            {
                sb.Append(" ---: |");
            }
            sb.AppendLine();

            foreach (var profile in profiles)
            {
                sb.Append(CultureInfo.InvariantCulture, $"| {profile} |");
                foreach (var c in columns)
                {
                    var text = byCell.TryGetValue((profile, c), out var cell)
                        ? (cell.Status == "ok" ? FormatWhole(value(cell.MedianMetrics)) : cell.Status)
                        : "-";
                    sb.Append(CultureInfo.InvariantCulture, $" {text} |");
                }
                sb.AppendLine();
            }
            sb.AppendLine();

            var peak = group.Where(r => r.Status == "ok").OrderByDescending(r => value(r.MedianMetrics)).FirstOrDefault();
            if (peak is not null)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"Peak: {FormatWhole(value(peak.MedianMetrics))} {unit} ({peakWhere(peak.Key)})."
                );
                sb.AppendLine();
            }
        }
    }

    private static void AppendLatency(StringBuilder sb, IReadOnlyList<BaselineCellResult> cells)
    {
        var rows = cells
            .Where(c => c.Key.Scenario == "latency")
            .OrderBy(c => c.Key.Provider, StringComparer.Ordinal)
            .ThenBy(c => ProfileOrder(c.Key.ExecutionProfile))
            .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        var multiProvider = rows.Select(r => r.Key.Provider).Distinct().Count() > 1;
        sb.AppendLine("## Latency: per-job round-trip ms (lower is better)");
        sb.AppendLine();
        sb.AppendLine("One executor, one job at a time.");
        sb.AppendLine();
        sb.AppendLine(multiProvider ? "| provider | profile | p50 | p95 | p99 |" : "| profile | p50 | p95 | p99 |");
        sb.AppendLine(multiProvider ? "| --- | --- | ---: | ---: | ---: |" : "| --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            var k = row.Key;
            var m = row.MedianMetrics;
            var lead = multiProvider ? $"| {k.Provider} | {k.ExecutionProfile} |" : $"| {k.ExecutionProfile} |";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{lead} {m.P50LatencyMs:F2} | {m.P95LatencyMs:F2} | {m.P99LatencyMs:F2} |");
        }
        sb.AppendLine();
    }

    private static void AppendEnqueue(StringBuilder sb, IReadOnlyList<BaselineCellResult> cells)
    {
        var rows = cells
            .Where(c => c.Key.Scenario == "enqueue")
            .OrderBy(c => c.Key.Provider, StringComparer.Ordinal)
            .ThenBy(c => c.Key.Workers)
            .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        sb.AppendLine("## Enqueue: single-call fleet insert jobs/s (higher is better)");
        sb.AppendLine();
        sb.AppendLine("N concurrent producers each enqueue one job at a time into the one namespace; worker idle (no draining).");
        sb.AppendLine();
        sb.AppendLine("| provider | producers | jobs/s | p95 ms/call |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            var m = row.MedianMetrics;
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {row.Key.Provider} | {row.Key.Workers} | {FormatWhole(m.EnqueuePerSecond)} | {m.P95LatencyMs:F2} |"
            );
        }
        sb.AppendLine();
    }

    private static void AppendEnqueueBatch(StringBuilder sb, IReadOnlyList<BaselineCellResult> cells)
    {
        var rows = cells
            .Where(c => c.Key.Scenario == "enqueue-batch")
            .OrderBy(c => c.Key.Provider, StringComparer.Ordinal)
            .ThenBy(c => c.Key.Workers)
            .ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        sb.AppendLine("## Enqueue (batch): fleet insert jobs/s (higher is better)");
        sb.AppendLine();
        sb.AppendLine("N concurrent producers each batch-enqueue their share into the one namespace; worker idle (no draining).");
        sb.AppendLine();
        sb.AppendLine("| provider | producers | jobs/s |");
        sb.AppendLine("| --- | ---: | ---: |");
        foreach (var row in rows)
        {
            var m = row.MedianMetrics;
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.Key.Provider} | {row.Key.Workers} | {FormatWhole(m.EnqueuePerSecond)} |");
        }
        sb.AppendLine();
    }

    private static void AppendQuery(StringBuilder sb, IReadOnlyList<BaselineCellResult> cells)
    {
        var rows = cells.Where(c => c.Key.Scenario == "query-list").OrderBy(c => c.Key.Provider, StringComparer.Ordinal).ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        sb.AppendLine("## Query: job-list read ms (lower is better)");
        sb.AppendLine();
        sb.AppendLine("Paged job-list read over a retained backlog.");
        sb.AppendLine();
        sb.AppendLine("| provider | retained rows | p50 | p95 | p99 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            var k = row.Key;
            var m = row.MedianMetrics;
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {k.Provider} | {k.Rows.ToString(CultureInfo.InvariantCulture)} | {m.P50LatencyMs:F2} | {m.P95LatencyMs:F2} | {m.P99LatencyMs:F2} |"
            );
        }
        sb.AppendLine();
    }

    private static string PolicyText(BaselinePolicy policy)
    {
        var warmup = policy.WarmupIterations == 1 ? "1 warmup" : $"{policy.WarmupIterations} warmups";
        var repeats = policy.MeasuredRepeats == 1 ? "1 measured run" : $"{policy.Aggregation} of {policy.MeasuredRepeats} measured runs";
        return $"{warmup}, {repeats}";
    }

    private static string FormatWhole(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}

public static class BaselineEnvironment
{
    public static BaselineEnvironmentInfo CaptureHost() =>
        new(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            CpuModel(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            DiskModel()
        );

    public static string FormatBytes(long bytes) =>
        bytes > 0 ? $"{(bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} GB" : "unknown";

    public static async Task<IReadOnlyList<BaselineDatabaseInfo>> CaptureDatabasesAsync(
        CancellationToken ct,
        IReadOnlyList<string>? providers = null
    )
    {
        var selected = BaselineSuite.NormalizeProviders(providers);
        var rows = new List<BaselineDatabaseInfo>(selected.Count);
        foreach (var provider in selected)
        {
            rows.Add(await CaptureDatabaseAsync(provider, ct).ConfigureAwait(false));
        }
        return rows;
    }

    public static string EngineVersion() =>
        typeof(JobsOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(JobsOptions).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string GitCommit() => Git("rev-parse --short HEAD");

    public static bool GitDirty() => !string.IsNullOrWhiteSpace(Git("status --porcelain"));

    private static async Task<BaselineDatabaseInfo> CaptureDatabaseAsync(string provider, CancellationToken ct)
    {
        var schema = BenchIdentity.NewSchema(DateTime.UtcNow, "env");
        var connectionString = ProviderConn.Resolve(provider, schema);
        var location = DatabaseLocation(provider, connectionString);
        var fingerprint = Fingerprint(provider, location);
        var serverVersion = await TryServerVersionAsync(provider, connectionString, ct).ConfigureAwait(false);
        return new BaselineDatabaseInfo(
            provider,
            serverVersion,
            MajorVersion(serverVersion),
            ProviderVersion(provider),
            location,
            fingerprint
        );
    }

    private static async Task<string> TryServerVersionAsync(string provider, string connectionString, CancellationToken ct)
    {
        try
        {
            if (LocalDatabase.IsSqlite(provider))
            {
                await using var c = new SqliteConnection(connectionString);
                await c.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT sqlite_version();";
                return Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "unknown";
            }
            if (LocalDatabase.IsPostgres(provider))
            {
                await using var c = new NpgsqlConnection(connectionString);
                await c.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SHOW server_version;";
                return Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "unknown";
            }

            await using var sc = new SqlConnection(connectionString);
            await sc.OpenAsync(ct).ConfigureAwait(false);
            await using var sql = sc.CreateCommand();
            sql.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));";
            return Convert.ToString(await sql.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) ?? "unknown";
        }
        catch (Exception ex)
            when (ex is SqliteException or NpgsqlException or SqlException or TimeoutException or InvalidOperationException)
        {
            return "unknown";
        }
    }

    private static string DatabaseLocation(string provider, string connectionString)
    {
        try
        {
            if (LocalDatabase.IsSqlite(provider))
            {
                var b = new SqliteConnectionStringBuilder(connectionString);
                var dir = Path.GetDirectoryName(Path.GetFullPath(b.DataSource)) ?? "";
                return $"sqlite:{dir}/acta-anvil-bench-*.db";
            }
            if (LocalDatabase.IsPostgres(provider))
            {
                var b = new NpgsqlConnectionStringBuilder(connectionString);
                return $"pg:{b.Host}:{b.Port}/{b.Database}";
            }

            var s = new SqlConnectionStringBuilder(connectionString);
            return $"mssql:{s.DataSource}/{s.InitialCatalog}";
        }
        catch
        {
            return $"{provider}:unknown";
        }
    }

    private static string Fingerprint(string provider, string location)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}|{location}"));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string ProviderVersion(string provider)
    {
        Assembly assembly =
            LocalDatabase.IsSqlite(provider) ? typeof(SqliteConnection).Assembly
            : LocalDatabase.IsPostgres(provider) ? typeof(NpgsqlConnection).Assembly
            : typeof(SqlConnection).Assembly;
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static string MajorVersion(string version)
    {
        var dot = version.IndexOf('.', StringComparison.Ordinal);
        var head = dot >= 0 ? version[..dot] : version;
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)
            ? major.ToString(CultureInfo.InvariantCulture)
            : "unknown";
    }

    private static string CpuModel()
    {
        // Windows: the registry carries the marketing name ("Intel(R) Core(TM) i9-14900K");
        // PROCESSOR_IDENTIFIER is only the family/model/stepping string, kept as a fallback.
        if (OperatingSystem.IsWindows())
        {
            var name =
                Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString",
                    null
                ) as string;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }

        var env = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        const string cpuInfo = "/proc/cpuinfo";
        if (File.Exists(cpuInfo))
        {
            var line = File.ReadLines(cpuInfo).FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                return colon >= 0 ? line[(colon + 1)..].Trim() : line.Trim();
            }
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    // Best-effort physical-disk model/brand so a reader can tell an NVMe/Optane/SATA-SSD apart when
    // comparing baselines (disk is the dominant variable for the sqlite cell). Windows asks
    // Get-PhysicalDisk; Linux reads /sys/block; anything else (and any failure) is "unknown".
    private static string DiskModel()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var output = RunCapture(
                    "powershell",
                    "-NoProfile",
                    "-Command",
                    "Get-PhysicalDisk | ForEach-Object { \"$($_.FriendlyName) [$($_.MediaType)]\" }"
                );
                var disks = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                if (disks.Length > 0)
                {
                    return string.Join("; ", disks);
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Directory.Exists("/sys/block"))
            {
                var disks = new List<string>();
                foreach (var dev in Directory.EnumerateDirectories("/sys/block"))
                {
                    var name = Path.GetFileName(dev);
                    if (name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var modelPath = Path.Combine(dev, "device", "model");
                    if (!File.Exists(modelPath))
                    {
                        continue;
                    }

                    var model = File.ReadAllText(modelPath).Trim();
                    var rotationalPath = Path.Combine(dev, "queue", "rotational");
                    var kind = File.Exists(rotationalPath) && File.ReadAllText(rotationalPath).Trim() == "0" ? "SSD" : "HDD";
                    if (model.Length > 0)
                    {
                        disks.Add($"{model} [{kind}]");
                    }
                }
                if (disks.Count > 0)
                {
                    return string.Join("; ", disks);
                }
            }
        }
        catch
        {
            // best-effort: any probe failure falls through to "unknown"
        }

        return "unknown";
    }

    private static string RunCapture(string fileName, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                return "";
            }

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch
        {
            return "";
        }
    }

    private static string Git(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                return "unknown";
            }

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            return output;
        }
        catch
        {
            return "unknown";
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BaselineFile))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, double>))]
internal sealed partial class BaselineJsonContext : JsonSerializerContext;
