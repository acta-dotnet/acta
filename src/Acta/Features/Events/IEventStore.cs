namespace Acta.Features.Events;

/// <summary>
/// Persistence port for event reads: the append-only ledger's keyset-paged list, job-scoped or
/// global. Event writes stay inside the atomic operations that cause them; this port owns reads
/// only. Implementations own command creation, parameter binding, row mapping, and the
/// two-result-sets-in-one-round-trip guarantee; the request arrives validated with the cursor
/// already decoded.
/// </summary>
internal interface IEventStore
{
    /// <summary>
    /// One keyset page of events ordered <c>created_at_utc DESC, id DESC</c> (optionally filtered by
    /// job, lineage root, namespace, event code, definition, or tenant) plus an opt-in total count
    /// over the same filter set, fetched in a single round trip.
    /// </summary>
    Task<EventPage> ListEventsAsync(EventPageRequest request, CancellationToken ct);
}

/// <summary>Validated, cursor-decoded request for one event page; <c>Take</c> carries the peek-ahead row.</summary>
internal sealed record EventPageRequest(
    long? JobId,
    long? LineageRootId,
    string? JobNamespace,
    JobEventCode? EventCode,
    int? JobDefinitionId,
    int? TenantId,
    int? WorkerId,
    JobActorCode? ActorCode,
    JobEventReasonCode? ReasonCode,
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc,
    DateTime? CursorCreatedAtUtc,
    long? CursorId,
    int Take,
    bool IncludeTotal,
    string? TagFiltersJson = null
);

/// <summary>One page of mapped event rows plus the opt-in filtered total.</summary>
internal sealed record EventPage(IReadOnlyList<JobEventListItem> Rows, long? Total);
