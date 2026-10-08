namespace Acta;

/// <summary>The next alertable events past the cursor, in (created_at_utc, id) order.</summary>
internal sealed record GetAlertableEventsResponse(IReadOnlyList<AlertableEvent> Events);
