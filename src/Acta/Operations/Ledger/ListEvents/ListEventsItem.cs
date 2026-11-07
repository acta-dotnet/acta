using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One event in a <see cref="ILedger.ListEventsAsync"/> page. The subject is <c>EntityKind</c> and <c>EntityId</c>,
/// the one numeric id on the wire, because the subject outlives its row: a purged job's events, and its
/// <c>job.purged</c> tombstone, still name it. The joined refs and names (<c>JobRef</c> of a job subject,
/// <c>LineageRootJobRef</c>, <c>WorkerRef</c>, <c>TenantName</c>, <c>JobName</c>) are null when absent or once their
/// row is gone. <c>Message</c> is the human text; <c>DetailsText</c> is the details payload decoded to text, null when
/// the event carries none or a non-json format.
/// </summary>
/// <param name="LineageRootJobId">The lineage root job's internal id; the JSON omits it and carries lineageRootJobRef.</param>
/// <param name="DefinitionId">The internal id of the definition the event concerns; the JSON omits it.</param>
/// <param name="TenantId">The internal id of the tenant the event concerns; the JSON omits it and carries tenantName.</param>
/// <param name="WorkerId">The internal id of the worker that recorded the event; the JSON omits it and carries workerRef.</param>
/// <param name="EventId">The event's id, which grows in the order events were written.</param>
/// <param name="EventCode">What happened, as a dotted code such as job.purged.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="JobNamespace">The namespace the event was recorded in.</param>
/// <param name="EntityKind">The kind of thing the event is about.</param>
/// <param name="EntityId">The internal numeric id of the thing the event is about; it is the one internal id the API carries, because a
/// purged job's events still name it.</param>
/// <param name="JobName">The name of the definition of the job the event is about, when it is about a job.</param>
/// <param name="JobRef">The ref of the job the event is about, when it is about a job.</param>
/// <param name="LineageRootJobRef">The ref of the job at the root of the lineage this job belongs to.</param>
/// <param name="TenantName">The tenant the job is about, when it names one.</param>
/// <param name="WorkerRef">The ref of the worker that recorded the event, when a worker did.</param>
/// <param name="ExecutionNumber">The attempt the event belongs to, when it belongs to one.</param>
/// <param name="DurationMs">How long the recorded work took, in milliseconds, when the event measures it.</param>
/// <param name="Message">The event's message, when it has one.</param>
/// <param name="DetailsText">The event's details as text, when it has any.</param>
public sealed record ListEventsItem(
    long EventId,
    EventCode EventCode,
    DateTime CreatedAtUtc,
    string JobNamespace,
    EntityCode EntityKind,
    long EntityId,
    string? JobName,
    JobRef? JobRef,
    [property: JsonIgnore] long? LineageRootJobId,
    JobRef? LineageRootJobRef,
    [property: JsonIgnore] int? DefinitionId,
    [property: JsonIgnore] int? TenantId,
    string? TenantName,
    [property: JsonIgnore] int? WorkerId,
    WorkerRef? WorkerRef,
    int? ExecutionNumber,
    int? DurationMs,
    string? Message,
    string? DetailsText
);
