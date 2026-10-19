using System.Text;

namespace Acta;

/// <summary>
/// One event list row. Detail bytes are decoded after binding: only text-shaped payload formats (json,
/// text) surface as text, using the same UTF-8 convention operators use in raw SQL; opaque formats
/// surface as absent text instead of mojibake because only the producing worker knows their schema.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record ListEventsRow(
    long JobEventId,
    EventCode EventCode,
    DateTime CreatedAtUtc,
    string JobNamespace,
    long? JobId,
    long? LineageRootId,
    int? DefinitionId,
    int WorkerId,
    int? ExecutionNumber,
    ActorCode ActorCode,
    string? Actor,
    JobStatusCode? FromStatus,
    JobStatusCode? ToStatus,
    ExecutionStatusCode? ExecutionStatus,
    int? DurationMs,
    EventReasonCode? ReasonCode,
    string? ReasonMessage,
    Guid? JobRef,
    Guid? LineageRootJobRef,
    int? TenantId,
    byte DetailFormatId,
    byte[]? Detail,
    string? JobName,
    Guid? WorkerRef,
    string? TenantName
)
{
    public ListEventsItem ToItem() =>
        new(
            JobEventId,
            EventCode,
            CreatedAtUtc,
            JobNamespace,
            JobName,
            JobId,
            JobRef is { } jobRef ? new JobRef(jobRef) : null,
            LineageRootId,
            LineageRootJobRef is { } rootRef ? new JobRef(rootRef) : null,
            DefinitionId,
            TenantId,
            WorkerId == 0 ? null : WorkerId,
            ExecutionNumber,
            ActorCode,
            Actor,
            FromStatus,
            ToStatus,
            ExecutionStatus,
            DurationMs,
            ReasonCode,
            ReasonMessage,
            Detail is not null && (DetailFormatId == JobPayloadFormat.Json.Id || DetailFormatId == JobPayloadFormat.Text.Id)
                ? Encoding.UTF8.GetString(Detail)
                : null,
            WorkerRef is { } workerRef ? new Acta.WorkerRef(workerRef) : null,
            TenantName
        );
}
