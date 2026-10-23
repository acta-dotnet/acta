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

/// <summary>An acknowledged root's terminal row, completed at its database completion stamp on the stopwatch.</summary>
internal sealed record Observation(string Key, long JobId, long CompletedTicks, int Status, string? Output);

/// <summary>A step's row; <c>ModifiedTicks</c> is its last state change in database ticks.</summary>
internal sealed record StepObservation(long JobId, string Name, int Status, string Result, long ModifiedTicks);

/// <summary>The settings a worker process resolved after applying the bench's overrides to the product defaults.</summary>
internal sealed record WorkerSettings(
    int Executors,
    double HeartbeatSeconds,
    int LeaseSeconds,
    double SafetyPollMs,
    double DeadAfterSeconds
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Admission[]))]
[JsonSerializable(typeof(Observation[]))]
[JsonSerializable(typeof(DurableRow[]))]
internal sealed partial class TrialJson : JsonSerializerContext;
