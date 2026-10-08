namespace Acta;

/// <summary>One generate-phase read: the namespace, the (stamp, id) cursor, the batch size, and the horizon.</summary>
internal sealed record GetAlertableEventsQuery(int NamespaceId, DateTime CursorUtc, long CursorEventId, int BatchSize, DateTime HorizonUtc);
