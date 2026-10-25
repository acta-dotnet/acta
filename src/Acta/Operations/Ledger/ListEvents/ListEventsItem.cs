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
