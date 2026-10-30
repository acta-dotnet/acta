using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.Verify;

/// <summary>
/// What a run executes: its suite, the suite's scenarios with its overrides applied, the databases, the seed, and the
/// harness constants of <c>Protocol/verify.json</c>. A replay runs the scenarios frozen here, not the files of its build.
/// </summary>
public sealed record RunPlan(
    string ProtocolId,
    string ProtocolSha256,
    string RunId,
    string SuiteName,
    int Seed,
    IReadOnlyList<string> Providers,
    IReadOnlyList<Scenario> Scenarios,
    Suite Suite,
    JsonElement Constants
)
{
    /// <summary>Whether measurement decides scenario statuses and the verdict. Where it does not, it runs and is reported.</summary>
    [JsonIgnore]
    public bool MeasurementGatesVerdict => Suite.GatesPerformance;

    /// <summary>The version of the measurement rules; two bundles compare only when theirs match.</summary>
    [JsonIgnore]
    public int MeasurementVersion => (int)Policy("measurement_version");

    [JsonIgnore]
    public int ObserverCadenceMs => (int)Policy("measurement", "observer_poll_ms");

    [JsonIgnore]
    public double GeneratorLagToleranceMs => Policy("measurement", "generator_lag_tolerance_ms");

    /// <summary>Reads the number <c>Protocol/verify.json</c> holds at <paramref name="path"/>.</summary>
    public double Policy(params string[] path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var element = Constants;
        foreach (var name in path)
        {
            if (!element.TryGetProperty(name, out element))
            {
                throw new InvalidOperationException($"Protocol/verify.json has no {string.Join('.', path)}.");
            }
        }
        return element.GetDouble();
    }

    /// <summary>The scenario named <paramref name="name"/>.</summary>
    public Scenario Scenario(string name) =>
        Scenarios.FirstOrDefault(s => s.Id == name) ?? throw new ArgumentException($"The plan has no scenario {name}.");

    /// <summary>
    /// Seconds a wait for <paramref name="jobs"/> logical jobs may last before it is a liveness failure, by the protocol's
    /// <c>liveness.deadline</c> rule; <paramref name="leaseSeconds"/> is the lease a kill leaves to lapse, zero when no
    /// worker is killed.
    /// </summary>
    public int Deadline(long jobs, double leaseSeconds, double? measuredDrain)
    {
        var rate = Policy("liveness", "minimum_progress_jobs_per_second");
        if (measuredDrain > 0)
        {
            rate = Math.Min(rate, measuredDrain.Value / Policy("liveness", "measured_drain_divisor"));
        }
        return (int)
            Math.Min(
                Policy("liveness", "deadline_ceiling_seconds"),
                Policy("liveness", "drain_seconds") + Policy("faults", "reclaim_lease_terms") * leaseSeconds + jobs / rate
            );
    }

    /// <summary>
    /// Whether a wait on job completions is a liveness failure, by the protocol's <c>liveness.drain</c> rule: past its
    /// <paramref name="deadline"/> it keeps waiting while its last completion landed within the stall window, and
    /// never past the ceiling.
    /// </summary>
    public bool DrainOverdue(double elapsedSeconds, double secondsSinceCompletion, int deadline) =>
        elapsedSeconds >= deadline
        && (
            secondsSinceCompletion >= Policy("liveness", "stall_seconds")
            || elapsedSeconds >= Policy("liveness", "deadline_ceiling_seconds")
        );

    /// <summary>
    /// Waits until <paramref name="remaining"/> reads zero jobs, counting each fall as a completion landing, and throws
    /// <see cref="TimeoutException"/> once the wait is <see cref="DrainOverdue"/>.
    /// </summary>
    public async Task DrainAsync(Func<Task<long>> remaining, int deadline, CancellationToken ct)
    {
        if (!await TryDrainAsync(remaining, _ => deadline, Stopwatch.GetTimestamp(), 25, ct))
        {
            throw new TimeoutException("Declared recovery bound elapsed.");
        }
    }

    /// <summary>
    /// Waits until <paramref name="remaining"/> reads zero jobs, counting each fall as a completion landing, and returns
    /// false once the wait, timed from <paramref name="since"/>, is <see cref="DrainOverdue"/> past the deadline the
    /// first count implies.
    /// </summary>
    public async Task<bool> TryDrainAsync(
        Func<Task<long>> remaining,
        Func<long, int> deadline,
        long since,
        int delayMs,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(remaining);
        ArgumentNullException.ThrowIfNull(deadline);
        var lastLanding = Stopwatch.GetTimestamp();
        var previous = long.MaxValue;
        int? limit = null;
        while (true)
        {
            var left = await remaining();
            var now = Stopwatch.GetTimestamp();
            if (left < previous)
            {
                lastLanding = now;
            }
            previous = left;
            limit ??= deadline(left);
            if (left == 0)
            {
                return true;
            }
            if (
                DrainOverdue(
                    Stopwatch.GetElapsedTime(since, now).TotalSeconds,
                    Stopwatch.GetElapsedTime(lastLanding, now).TotalSeconds,
                    limit.Value
                )
            )
            {
                return false;
            }
            await Task.Delay(delayMs, ct);
        }
    }

    /// <summary>Whether PostgreSQL and SQL Server run on the acta-dev Compose servers instead of owned containers.</summary>
    public bool UseComposeServers { get; init; }
}

public sealed record Metric(double? Value, string Unit, string Status = "MEASURED", string? Reason = null)
{
    public static Metric Missing(string unit, string reason) => new(null, unit, "UNAVAILABLE", reason);
}

public sealed record CaseResult(
    string Id,
    string Provider,
    string Phase,
    int Repetition,
    string Status,
    string? Reason,
    string Safety,
    string Liveness,
    string Coverage,
    string Measurement,
    Dictionary<string, Metric> Metrics,
    IReadOnlyList<string> Evidence,
    string NativeTopology = "acta",
    bool Instrumented = false
)
{
    public static CaseResult Incomplete(string id, string provider, string phase, string reason) =>
        new(id, provider, phase, 0, "INCONCLUSIVE", reason, "UNVERIFIED", "UNVERIFIED", "INCOMPLETE", "NOT_MEASURED", [], []);

    /// <summary>
    /// A gap in capacity measurement: incomplete where the plan's suite gates its verdict on measurement, otherwise a
    /// not-applicable row that still reports the gap.
    /// </summary>
    public static CaseResult MeasurementGap(RunPlan plan, string id, string provider, string phase, string reason) =>
        plan.MeasurementGatesVerdict
            ? Incomplete(id, provider, phase, reason)
            : NotApplicable(id, provider, Verdicts.Informational(plan, reason), phase);

    /// <summary>A scenario that does not run on <paramref name="provider"/>, with the reason it says.</summary>
    public static CaseResult NotApplicable(string id, string provider, string reason, string phase = "skip") =>
        new(id, provider, phase, 0, "NOT_APPLICABLE", reason, "NOT_APPLICABLE", "NOT_APPLICABLE", "NOT_APPLICABLE", "NOT_MEASURED", [], []);
}

public sealed record Provenance(
    string ProtocolId,
    string ProtocolSha256,
    string PlanSha256,
    string SourceCommit,
    bool SourceDirty,
    IReadOnlyList<string> BinarySha256,
    string EnvironmentFile
);

public sealed record RunScope(
    IReadOnlyList<string> RequestedProviders,
    IReadOnlyList<string> CompletedProviders,
    IReadOnlyList<string> ExcludedFaultClasses
);

public sealed record RunCoverage(int RequiredInstances, int PassedInstances, int FailedInstances, int UnresolvedRequiredInstances);

public sealed record Durability(string Track, bool SettingsVerified, bool PhysicalPowerLossTested);

public sealed record Artifact(string Path, string Sha256);

public sealed record RunResult(
    string SchemaVersion,
    string RunId,
    string EvidenceKind,
    string Status,
    Provenance Provenance,
    RunScope Scope,
    RunCoverage Coverage,
    Durability Durability,
    IReadOnlyList<CaseResult> Cases,
    IReadOnlyList<Artifact> Artifacts,
    bool HarnessError = false
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RunPlan))]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(CaseResult))]
[JsonSerializable(typeof(CaseResult[]))]
[JsonSerializable(typeof(PerformanceCellSummary[]))]
[JsonSerializable(typeof(Suite))]
[JsonSerializable(typeof(WorkShape[]))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ExpectedOutcome[]))]
[JsonSerializable(typeof(ObservedOutcome[]))]
[JsonSerializable(typeof(Check[]))]
[JsonSerializable(typeof(HistogramSnapshot))]
[JsonSerializable(typeof(FaultEvidence))]
public partial class VerifyJson : JsonSerializerContext;
