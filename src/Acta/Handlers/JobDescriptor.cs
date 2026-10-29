using System.Collections.Immutable;

namespace Acta;

/// <summary>
/// Generator-emitted per-handler descriptor. Every field is derived from the handler's
/// compile-time signature plus its <c>[Job]</c> attribute; the runtime does not re-infer result
/// behavior at invocation time.
/// </summary>
public sealed record JobDescriptor(
    string JobName,
    Type HandlerType,
    string MethodName,
    Type InputType,
    Type? OutputType,
    JobPayloadFormat InputPayloadFormat,
    JobPayloadFormat? OutputPayloadFormat,
    JobPriorityCode Priority,
    int MaxAttempts,
    EventRecordingCode EventRecording,
    AlertProfileCode AlertProfile,
    FiringFailurePolicyCode FiringFailurePolicy,
    ScheduledDeliveryCode ScheduledDelivery,
    JobHandlerInvokeDelegate Invoker,
    Func<IJobPayloadSerializer, JobPayload, object> DeserializeInput,
    Func<IJobPayloadSerializer, object?, JobPayload>? SerializeOutput
)
{
    /// <summary>
    /// Whether jobs of this definition must, may, or must not carry a tenant; synced from
    /// <c>[Job(TenantRequirement = ...)]</c> and enforced at the enqueue boundary in the database.
    /// </summary>
    public DefinitionTenantRequirementCode TenantRequirement { get; init; } = DefinitionTenantRequirementCode.Optional;

    /// <summary>
    /// How many attempts of this definition may execute at once. Null = unlimited unless the job row
    /// carries a <c>ConcurrencyKey</c>, which alone admits one at a time. Overlaid with the
    /// definition's effective value at startup and on every policy reload.
    /// </summary>
    public int? ConcurrencyLimit { get; init; }

    /// <summary>
    /// How often attempts of this definition may start, as <c>N/s</c> / <c>N/m</c> / <c>N/h</c>; null =
    /// no rate limit. Overlaid with the definition's effective value at startup and on every policy
    /// reload, and parsed into <see cref="Rate"/> as it is set so admission never parses.
    /// </summary>
    public string? RateLimit
    {
        get => _rateLimit;
        init
        {
            _rateLimit = value;
            // Never throws, so a value that bypassed the gates cannot fail a policy reload: the text is
            // kept for the gates to reject and admission simply reads no rate.
            Rate = RateLimitSpec.TryParse(value, out var parsed, out _) ? parsed : null;
        }
    }

    private readonly string? _rateLimit;

    /// <summary>The parsed <see cref="RateLimit"/>, or null when this definition declares no usable rate.</summary>
    internal RateLimitSpec? Rate { get; private init; }

    /// <summary>
    /// Meter this definition's <see cref="RateLimit"/> spends from; null means the definition name.
    /// Code-owned, so unlike the rate it is never overlaid from an operator override.
    /// </summary>
    public string? RateName { get; init; }

    /// <summary>
    /// Default lane for this definition's jobs, from <c>[Job(Lane = ...)]</c>; null means unlaned.
    /// Code-owned, so it is never overlaid from an operator override.
    /// </summary>
    public string? Lane { get; init; }

    /// <summary>
    /// Declared recurring schedules (one per <c>[JobSchedule]</c>). Empty for non-scheduled jobs.
    /// </summary>
    public ImmutableArray<ScheduleDescriptor> Schedules { get; init; } = [];

    /// <summary>
    /// Factory for a default input instance, used to seed the recurring slot's stored payload.
    /// Emitted only for scheduled jobs whose input is default-constructible; null otherwise.
    /// </summary>
    public Func<object>? CreateDefaultInput { get; init; }

    /// <summary>
    /// Serializes a slot input instance to its wire payload using the same conventions as the
    /// normal enqueue flow. Emitted alongside <see cref="CreateDefaultInput"/>; null otherwise.
    /// </summary>
    public Func<IJobPayloadSerializer, object, JobPayload>? SerializeInput { get; init; }

    /// <summary>
    /// Retry backoff policy as an Acta backoff expression, e.g. <c>"1m..8h x2 ~10%"</c>. Null =
    /// framework default (<c>"1m..1d x2 ~10%"</c>) at registration.
    /// </summary>
    public string? Backoff { get; init; }

    /// <summary>
    /// Per-attempt wall-clock cap in whole seconds. Null = framework default at registration.
    /// </summary>
    public int? ExecutionTimeoutSeconds { get; init; }

    /// <summary>
    /// Whole-job deadline in whole seconds from job creation. Null = no deadline. Spans retries,
    /// unlike <see cref="ExecutionTimeoutSeconds"/>.
    /// </summary>
    public int? DeadlineSeconds { get; init; }

    /// <summary>
    /// How the engine treats a job past its deadline. Default <c>Strict</c>; meaningful only when
    /// <see cref="DeadlineSeconds"/> is set.
    /// </summary>
    public DeadlineBehaviorCode DeadlineBehavior { get; init; } = DeadlineBehaviorCode.Strict;

    /// <summary>
    /// How long terminal Jobs are retained, in whole seconds. Null = framework default at registration.
    /// </summary>
    public int? JobRetentionSeconds { get; init; }

    /// <summary>
    /// Operator-stable alert channel name this definition's alerts route to. Null = none.
    /// </summary>
    public string? AlertChannelName { get; init; }

    /// <summary>
    /// Runbook URL surfaced on alerts and the operator dashboard. Null = none.
    /// </summary>
    public string? RunbookUrl { get; init; }

    /// <summary>Human display label from the attribute; null when unset.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Operator-facing description from the attribute; null when unset.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Compile-time JSON skeleton of <see cref="InputType"/>: a shape hint for operator tooling (the
    /// dashboard seeds its enqueue editor with it), never a contract and never a default value. Every
    /// value is an empty stand-in. Emitted for Json-format inputs with settable members; null otherwise.
    /// </summary>
    public string? InputTemplateJson { get; init; }
}
