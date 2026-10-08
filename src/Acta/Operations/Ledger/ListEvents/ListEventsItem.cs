using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One audit event row in a <see cref="ILedger.ListEventsAsync"/> page. JSON carries the public refs and hides the
/// numeric ids. Events outlive their job: <c>JobRef</c> is denormalized on the event row and survives the purge, as
/// does <c>JobName</c>, resolved from the catalog; <c>LineageRootJobRef</c>, <c>TenantKey</c>, and <c>WorkerRef</c> are
/// null when absent or purged, and a worker actor's <c>ActorKey</c> is its public ref. <c>DetailText</c> is the
/// free-form or structured detail decoded to text, null when the event carries none or a non-text format.
/// </summary>
public sealed record ListEventsItem(
    long JobEventId,
    EventCode EventCode,
    DateTime CreatedAtUtc,
    string JobNamespace,
    string? JobName,
    [property: JsonIgnore] long? JobId,
    JobRef? JobRef,
    [property: JsonIgnore] long? LineageRootId,
    JobRef? LineageRootJobRef,
    [property: JsonIgnore] int? DefinitionId,
    [property: JsonIgnore] int? TenantId,
    [property: JsonIgnore] int? WorkerId,
    int? ExecutionNumber,
    ActorCode ActorCode,
    string? ActorKey,
    JobStatusCode? FromStatus,
    JobStatusCode? ToStatus,
    ExecutionStatusCode? ExecutionStatus,
    int? DurationMs,
    EventReasonCode? ReasonCode,
    string? ReasonMessage,
    string? DetailText,
    WorkerRef? WorkerRef,
    string? TenantKey
);
