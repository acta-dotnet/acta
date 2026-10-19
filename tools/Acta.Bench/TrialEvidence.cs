using System.Text.Json.Serialization;

namespace Acta.Bench;

internal sealed record Admission(
    string Key,
    long ScheduledTicks,
    long? SubmittedTicks,
    long? AcknowledgedTicks,
    long? JobId,
    string Outcome,
    int BatchSize = 1
);

internal sealed record Observation(
    string Key,
    long JobId,
    long ObservedTicks,
    int Status,
    long? ParentId = null,
    string? Output = null,
    string? JobNamespace = null,
    string? DurableKey = null
);

internal sealed record StepObservation(long JobId, string Name, int Status, string Result, long ObservedTicks);

/// <summary>The settings a worker process resolved after applying the bench's overrides to the product defaults.</summary>
internal sealed record WorkerSettings(
    int Executors,
    double HeartbeatSeconds,
    int LeaseSeconds,
    double SafetyPollMs,
    double DeadAfterSeconds
);

internal sealed record TrialTelemetry(
    long Timestamp,
    double ControllerCpuSeconds,
    long ControllerWorkingSetBytes,
    int Backlog,
    double ObserverCycleMs
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(Admission))]
[JsonSerializable(typeof(Admission[]))]
[JsonSerializable(typeof(Observation))]
[JsonSerializable(typeof(Observation[]))]
[JsonSerializable(typeof(StepObservation[]))]
[JsonSerializable(typeof(TrialTelemetry[]))]
[JsonSerializable(typeof(BacklogSample[]))]
[JsonSerializable(typeof(DurableRow[]))]
internal sealed partial class TrialJson : JsonSerializerContext;
