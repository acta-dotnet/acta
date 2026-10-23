using System.Text;

namespace Acta;

/// <summary>
/// One event list row. Details bytes are decoded after binding: only text-shaped payload formats (json,
/// text) surface as text, using the same UTF-8 convention operators use in raw SQL; opaque formats
/// surface as absent text instead of mojibake because only the producing worker knows their schema.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record ListEventsRow(
    long EventId,
    EventCode EventCode,
    DateTime CreatedAtUtc,
    string JobNamespace,
    EntityCode EntityCode,
    long EntityId,
    string? JobName,
    Guid? JobRef,
    long? LineageRootJobId,
    Guid? LineageRootJobRef,
    int? DefinitionId,
    int? TenantId,
    string? TenantName,
    int WorkerId,
    Guid? WorkerRef,
    int? ExecutionNumber,
    int? DurationMs,
    string? EventMessage,
    byte DetailsFormatId,
    byte[]? Details
)
{
    public ListEventsItem ToItem() =>
        new(
            EventId,
            EventCode,
            CreatedAtUtc,
            JobNamespace,
            EntityCode,
            EntityId,
            JobName,
            JobRef is { } jobRef ? new JobRef(jobRef) : null,
            LineageRootJobId,
            LineageRootJobRef is { } rootRef ? new JobRef(rootRef) : null,
            DefinitionId,
            TenantId,
            TenantName,
            WorkerId == 0 ? null : WorkerId,
            WorkerRef is { } workerRef ? new Acta.WorkerRef(workerRef) : null,
            ExecutionNumber,
            DurationMs,
            EventMessage,
            Details is not null && (DetailsFormatId == JobPayloadFormat.Json.Id || DetailsFormatId == JobPayloadFormat.Text.Id)
                ? Encoding.UTF8.GetString(Details)
                : null
        );
}
