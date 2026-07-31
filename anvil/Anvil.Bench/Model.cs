using System.Diagnostics;
using Acta;

namespace Anvil.Bench;

/// <summary>
/// The parameter set for one benchmark cell. A run expands comma-separated CLI lists into the
/// cartesian product of these, one cell each. <c>Workers</c> drives the multi-worker scenarios and
/// <c>Rows</c> the opt-in big-data ones; both default so the original scenarios are unaffected.
/// <c>Profile</c> selects the execution profile (Buffered/Direct/Bulk) for the comparison scenarios.
/// </summary>
public sealed record CellParams(
    string Provider,
    int Jobs,
    int Executors,
    int ClaimBatch,
    int PayloadBytes,
    int Iterations,
    int Workers = 1,
    int Rows = 0,
    ExecutionProfile Profile = ExecutionProfile.Direct
);

/// <summary>
/// The measured metrics for one cell. Rates are jobs/sec; latencies are milliseconds. Fields that a
/// given scenario does not measure are left at zero. <c>Extra</c> carries scenario-specific scalars
/// (recovery ms, query ms, purge seconds, fairness, etc.) keyed by name; null when unused.
/// </summary>
public sealed record CellMetrics(
    double EnqueueRatePerSec,
    double EndToEndRatePerSec,
    double DrainRatePerSec,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyP99Ms,
    double LatencyMaxMs,
    double LatencyMeanMs,
    double EnqueueSeconds,
    double DrainSeconds,
    int JobsObserved,
    IReadOnlyDictionary<string, double>? Extra = null
);

/// <summary>
/// Run-wide settings that are not swept per cell: the recovery lease window, the wakeup poll floor,
/// and an optional Redis wakeup connection. Threaded to every scenario alongside its <see cref="CellParams"/>.
/// </summary>
public sealed record BenchConfig(
    int LeaseTtlSeconds = 5,
    TimeSpan? SafetyPollInterval = null,
    string? RedisConfig = null,
    int WorkMs = 0,
    bool SharedKey = false,
    string LoadProfile = "constant",
    int DurationSec = 60,
    int RatePerSec = 2000,
    // Run the throughput/drain workload on the audit-on handler so internal probes can measure the
    // per-job event write cost. Cell keys do not record this value.
    bool AuditOn = false
);

/// <summary>
/// One scenario run at one parameter combination: the inputs, the metrics, and whether it completed.
/// </summary>
public sealed record CellResult(string Scenario, CellParams Params, CellMetrics Metrics, string Status, string? Note);

/// <summary>
/// Percentile and rate helpers over Stopwatch-tick latency samples.
/// </summary>
public static class Stats
{
    /// <summary>Converts Stopwatch ticks to milliseconds.</summary>
    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Returns p50, p95, p99, max and mean (all milliseconds) over the given tick samples. A null or
    /// empty input yields all zeros.
    /// </summary>
    public static (double P50, double P95, double P99, double Max, double Mean) Percentiles(long[] ticks)
    {
        if (ticks.Length == 0)
        {
            return (0, 0, 0, 0, 0);
        }

        Array.Sort(ticks);
        double At(double q) => Ms(ticks[Math.Clamp((int)(q * ticks.Length), 0, ticks.Length - 1)]);

        double sum = 0;
        foreach (var t in ticks)
        {
            sum += Ms(t);
        }

        return (At(0.50), At(0.95), At(0.99), Ms(ticks[^1]), sum / ticks.Length);
    }

    /// <summary>
    /// Rate in items per second, guarding against a zero or negative window.
    /// </summary>
    public static double RatePerSec(int count, double seconds) => seconds > 0 ? count / seconds : 0;
}
