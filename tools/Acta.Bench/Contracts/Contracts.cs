using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.Bench;

public sealed record ScenarioCatalog(string SchemaVersion, string Description, IReadOnlyList<ScenarioDescriptor> Cases);

public sealed record ScenarioDescriptor(
    string Id,
    string Family,
    string Name,
    string Workload,
    IReadOnlyList<string> Phases,
    IReadOnlyList<string> Requirements,
    bool RequiredInFull,
    IReadOnlyList<string> SafetyInvariants,
    IReadOnlyList<string> RequiredEvidence,
    string Applicability,
    string? MeasureUnit = null,
    string? Subject = null
);

public sealed record TrialCell(
    string Name,
    string CaseId,
    int? Executors = null,
    int Workers = 1,
    int? PayloadBytes = null,
    int Namespaces = 1,
    int? BatchSize = null
);

public sealed record RunPlan(
    string ProtocolId,
    string ProtocolSha256,
    string RunId,
    string Profile,
    int Seed,
    IReadOnlyList<string> Providers,
    IReadOnlyList<ScenarioDescriptor> Cases,
    int WarmupSeconds,
    int MeasurementSeconds,
    int Repetitions,
    int SoakSeconds,
    int ObserverCadenceMs,
    int ProducerConcurrency,
    int BufferCapacity,
    int DrainSeconds,
    double ReferenceSloMs,
    JsonElement FullPolicy
)
{
    [JsonIgnore]
    public bool IsExtended => Profile == "full";

    /// <summary>
    /// Whether capacity measurement decides case statuses and the verdict in this plan's profile. Where it does not,
    /// measurement still runs and is reported, and only correctness decides.
    /// </summary>
    [JsonIgnore]
    public bool MeasurementGatesVerdict =>
        FullPolicy.GetProperty("profiles").GetProperty(Profile).GetProperty("measurement_gates_verdict").GetBoolean();

    /// <summary>Reads the number the frozen protocol declares at <paramref name="path"/>.</summary>
    public double Policy(params string[] path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var element = FullPolicy;
        foreach (var name in path)
        {
            element = element.GetProperty(name);
        }
        return element.GetDouble();
    }

    /// <summary>Reads the number the frozen protocol declares for this plan's profile.</summary>
    public double ProfilePolicy(string name) => Policy("profiles", Profile, name);

    /// <summary>The worker processes F09 runs on <paramref name="provider"/> in this plan's profile.</summary>
    public int ChaosWorkers(string provider) => (int)Policy("profiles", Profile, "chaos_workers", provider);

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
                DrainSeconds + Policy("faults", "reclaim_lease_terms") * leaseSeconds + jobs / rate
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
        ArgumentNullException.ThrowIfNull(remaining);
        var watch = Stopwatch.StartNew();
        var lastCompletion = TimeSpan.Zero;
        var previous = long.MaxValue;
        while (await remaining() is var left && left > 0)
        {
            if (left < previous)
            {
                lastCompletion = watch.Elapsed;
            }
            previous = left;
            if (DrainOverdue(watch.Elapsed.TotalSeconds, (watch.Elapsed - lastCompletion).TotalSeconds, deadline))
            {
                throw new TimeoutException("Declared recovery bound elapsed.");
            }
            await Task.Delay(25, ct);
        }
    }

    /// <summary>The setting the frozen protocol overrides for this plan's profile; null keeps the product default.</summary>
    public double? ProfileOverride(string name) =>
        FullPolicy.GetProperty("profiles").GetProperty(Profile).GetProperty(name) is { ValueKind: not JsonValueKind.Null } value
            ? value.GetDouble()
            : null;

    /// <summary>The trials a cell adds when its first <see cref="Repetitions"/> break the dispersion rule.</summary>
    [JsonIgnore]
    public int AdditionalRepetitions => (int)Policy("measurement", "additional_repetitions_for_predeclared_instability");

    /// <summary>The independent repetitions of each targeted fault boundary in this plan's profile.</summary>
    [JsonIgnore]
    public int TargetedFaultRepetitions => (int)ProfilePolicy("targeted_fault_repetitions");

    public IReadOnlyList<string> RequestedDiagnosticCells { get; init; } = [];
    public IReadOnlyDictionary<string, int> PreloadedBacklogRoots { get; init; } = new Dictionary<string, int>();
    public int AdmissionBatchSize { get; init; }
    public double GeneratorLagToleranceMs { get; init; }
    public IReadOnlyList<TrialCell> DiagnosticCells { get; init; } = [];
    public int ChaosSeed { get; init; }
    public int ChaosJobs { get; init; }
    public IReadOnlyDictionary<string, double> LatencySloMs { get; init; } = new Dictionary<string, double>();
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
    bool Instrumented = false,
    string? DiagnosticCellName = null
)
{
    public static CaseResult Incomplete(string id, string provider, string phase, string reason) =>
        new(id, provider, phase, 0, "INCONCLUSIVE", reason, "UNVERIFIED", "UNVERIFIED", "INCOMPLETE", "NOT_MEASURED", [], []);

    /// <summary>
    /// A gap in capacity measurement: incomplete where the plan's profile gates its verdict on measurement, otherwise a
    /// not-applicable row that still reports the gap.
    /// </summary>
    public static CaseResult MeasurementGap(RunPlan plan, string id, string provider, string phase, string reason) =>
        plan.MeasurementGatesVerdict
            ? Incomplete(id, provider, phase, reason)
            : new(
                id,
                provider,
                phase,
                0,
                "NOT_APPLICABLE",
                Verdicts.Informational(plan, reason),
                "NOT_APPLICABLE",
                "NOT_APPLICABLE",
                "NOT_APPLICABLE",
                "NOT_MEASURED",
                [],
                []
            );
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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(ScenarioCatalog))]
[JsonSerializable(typeof(RunPlan))]
[JsonSerializable(typeof(RunResult))]
[JsonSerializable(typeof(CaseResult))]
[JsonSerializable(typeof(CaseResult[]))]
[JsonSerializable(typeof(PerformanceCellSummary[]))]
[JsonSerializable(typeof(TrialCell[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ExpectedOutcome[]))]
[JsonSerializable(typeof(ObservedOutcome[]))]
[JsonSerializable(typeof(Check[]))]
[JsonSerializable(typeof(HistogramSnapshot))]
[JsonSerializable(typeof(FaultEvidence))]
public partial class BenchJson : JsonSerializerContext;
