using System.Text.Json.Serialization;

namespace Acta.Verify;

/// <summary>A step's row; <c>ModifiedTicks</c> is its last state change in database ticks.</summary>
internal sealed record StepObservation(long JobId, string Name, int Status, string Result, long ModifiedTicks);

/// <summary>The settings a worker resolved after applying the harness's overrides to the product defaults.</summary>
internal sealed record WorkerSettings(
    int Executors,
    double HeartbeatSeconds,
    int LeaseSeconds,
    double SafetyPollMs,
    double DeadAfterSeconds
)
{
    public static WorkerSettings From(JobsOptions options) =>
        new(
            options.MaxConcurrentExecutors,
            options.HeartbeatInterval.TotalSeconds,
            options.LeaseTtlSeconds,
            options.SafetyPollInterval.TotalMilliseconds,
            options.WorkerDeadAfter.TotalSeconds
        );
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DurableRow[]))]
internal sealed partial class TrialJson : JsonSerializerContext;
