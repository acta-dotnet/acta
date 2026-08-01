using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One audit event row in a <see cref="ILedger.ListEventsAsync"/> page. JSON serialization
/// carries the public job refs and hides the numeric job ids; events outlive their job, and each
/// ref parameter documents whether it survives the purge.
/// </summary>
/// <param name="JobEventId">Event row id.</param> <param name="EventCode">What happened.</param> <param name="CreatedAtUtc">When it happened.</param>
/// <param name="JobNamespace">Owning namespace name.</param> <param name="JobName">Subject job's definition name resolved from the catalog (survives the purge), or null.</param> <param name="JobId">Subject job id, or null for namespace-level events.</param>
/// <param name="JobRef">Subject job's public ref, or null for namespace-level events; denormalized on the event row, so it survives the job's purge.</param>
/// <param name="LineageRootId">Root id of the subject's lineage tree, or null.</param> <param name="LineageRootJobRef">Lineage root's public ref, or null when there is no lineage or the root row was purged.</param>
/// <param name="JobDefinitionId">Catalog definition id, or null.</param> <param name="TenantId">Tenant id for job-scoped events, or null.</param>
/// <param name="WorkerId">Acting worker id, or null.</param> <param name="ExecutionNumber">Attempt the event belongs to, or null.</param>
/// <param name="ActorCode">Who initiated the transition.</param> <param name="ActorKey">Actor identity text, or null.</param>
/// <param name="FromStatus">Status before the transition, or null.</param> <param name="ToStatus">Status after the transition, or null.</param>
/// <param name="ExecutionStatus">Execution outcome attached to the event, or null.</param> <param name="DurationMs">Execution duration, or null.</param>
/// <param name="ReasonCode">Reason recorded with the event, or null.</param> <param name="ReasonMessage">Reason text, or null.</param>
/// <param name="DetailText">Free-form (text) or structured (json) detail decoded to text, or null when the event carries no detail or a non-text format.</param>
public sealed record JobEventListItem(
    long JobEventId,
    JobEventCode EventCode,
    DateTime CreatedAtUtc,
    string JobNamespace,
    string? JobName,
    [property: JsonIgnore] long? JobId,
    JobRef? JobRef,
    [property: JsonIgnore] long? LineageRootId,
    JobRef? LineageRootJobRef,
    int? JobDefinitionId,
    int? TenantId,
    int? WorkerId,
    int? ExecutionNumber,
    JobActorCode ActorCode,
    string? ActorKey,
    JobStatusCode? FromStatus,
    JobStatusCode? ToStatus,
    ExecutionStatusCode? ExecutionStatus,
    int? DurationMs,
    JobEventReasonCode? ReasonCode,
    string? ReasonMessage,
    string? DetailText
);
