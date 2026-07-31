using System.Text;

namespace Acta.Runtime.Modules.Operations.Events;

/// <summary>
/// Flat event list row in SELECT order, shared by the provider event stores so the generated binder
/// and the column contract stay single-sourced. Detail bytes are decoded after binding: only
/// text-shaped payload formats (json, text) surface as text, using the same UTF-8 convention
/// operators use in raw SQL; opaque formats surface as absent text instead of mojibake because only
/// the producing worker knows their schema.
/// </summary>
internal sealed record EventListProjectionRow(
    long JobEventId,
    JobEventCode EventCode,
    DateTime CreatedAtUtc,
    string JobNamespace,
    long? JobId,
    long? LineageRootId,
    int? DefinitionId,
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
    Guid? JobRef,
    Guid? LineageRootJobRef,
    int? TenantId,
    byte DetailFormatId,
    byte[]? Detail
)
{
    public JobEventListItem ToListItem() =>
        new(
            JobEventId,
            EventCode,
            CreatedAtUtc,
            JobNamespace,
            JobId,
            JobRef is { } jobRef ? new JobRef(jobRef) : null,
            LineageRootId,
            LineageRootJobRef is { } rootRef ? new JobRef(rootRef) : null,
            DefinitionId,
            TenantId,
            WorkerId,
            ExecutionNumber,
            ActorCode,
            ActorKey,
            FromStatus,
            ToStatus,
            ExecutionStatus,
            DurationMs,
            ReasonCode,
            ReasonMessage,
            Detail is not null && (DetailFormatId == JobPayloadFormat.Json.Id || DetailFormatId == JobPayloadFormat.Text.Id)
                ? Encoding.UTF8.GetString(Detail)
                : null
        );
}
