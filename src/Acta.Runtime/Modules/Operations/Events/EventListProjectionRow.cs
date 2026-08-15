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
    EventCode EventCode,
    DateTime CreatedAtUtc,
    string JobNamespace,
    long? JobId,
    long? LineageRootId,
    int? DefinitionId,
    int? WorkerId,
    int? ExecutionNumber,
    ActorCode ActorCode,
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
    byte[]? Detail,
    string? JobName,
    Guid? WorkerRef,
    string? TenantKey
)
{
    public EventListItem ToListItem() =>
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
            WorkerId,
            ExecutionNumber,
            ActorCode,
            CanonicalActorKey(),
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
            TenantKey
        );

    // Worker-actor rows persist the acting worker's public ref as the raw uuid text the emitting SQL
    // cast it to. Render it in the same canonical wrk_ form operators see everywhere else, leaving
    // every other actor's key exactly as stored.
    private string? CanonicalActorKey() =>
        ActorCode == ActorCode.Worker && Guid.TryParse(ActorKey, out var parsed) ? new Acta.WorkerRef(parsed).ToString() : ActorKey;
}
