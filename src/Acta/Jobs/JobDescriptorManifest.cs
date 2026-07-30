using System.Collections.Immutable;

namespace Acta;

/// <summary>
/// Generator-emitted manifest of every <see cref="JobDescriptor"/> declared in an
/// <see cref="IJobManifest"/>-bearing assembly. Source-time facts only.
/// </summary>
public sealed record JobDescriptorManifest(ImmutableArray<JobDescriptor> Descriptors);

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
    JobInvocationKind InvocationKind,
    bool RequiresJobContextParameter,
    bool RequiresCancellationToken,
    JobPriorityCode Priority,
    short MaxAttempts,
    JobAuditLevelCode AuditLevel,
    JobAlertProfileCode AlertProfile,
    JobHandlerInvokeDelegate Invoker,
    Func<IJobPayloadSerializer, JobPayload, object> DeserializeInput,
    Func<IJobPayloadSerializer, object?, JobPayload>? SerializeOutput
)
{
    /// <summary>
    /// Whether jobs of this definition must, may, or must not carry a tenant; synced from
    /// <c>[Job(TenantRequirement = ...)]</c> and enforced at the enqueue boundary in the database.
    /// </summary>
    public JobTenantRequirementCode TenantRequirement { get; init; } = JobTenantRequirementCode.Optional;

    /// <summary>
    /// Declared recurring schedules (one per <c>[JobSchedule]</c>). Empty for non-scheduled jobs.
    /// </summary>
    public ImmutableArray<JobScheduleDescriptor> Schedules { get; init; } = [];

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
    /// Maximum <c>results</c> rows retained for the recurring slot (newest by
    /// <c>execution_number</c>). Runtime-driven; never snapshotted onto <c>job</c>. Default 1.
    /// </summary>
    public int RecurringResultCap { get; init; } = 1;

    /// <summary>
    /// Retry backoff policy as an Acta backoff expression, e.g. <c>"1m..8h x2 ~10%"</c>. Null =
    /// framework default (<c>"1m..8h"</c>) at registration.
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

/// <summary>
/// Generator-emitted descriptor for one declared <c>[JobSchedule]</c>. Source-time facts only;
/// next-occurrence instants are computed by the runtime, never here.
/// </summary>
public sealed record JobScheduleDescriptor(
    string JobName,
    string ScheduleName,
    string Expression,
    string? TimeZone,
    MisfireStrategyCode Misfire,
    ScheduleExpressionKindCode ExpressionKind,
    string? Description,
    ImmutableArray<string> Environments
);
