namespace Acta;

/// <summary>The skipped events still on the ledger, in (created_at_utc, event_id) order.</summary>
internal sealed record GetPoisonAlertEventsResponse(IReadOnlyList<AlertableEvent> Events);
