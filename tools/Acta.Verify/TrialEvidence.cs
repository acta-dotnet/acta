using System.Text.Json.Serialization;

namespace Acta.Verify;

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
[JsonSerializable(typeof(DurableRow[]))]
internal sealed partial class TrialJson : JsonSerializerContext;
