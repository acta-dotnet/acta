namespace Acta;

/// <summary>The job whose open failure alerts a success or a final failure resolves, and that event's id.</summary>
internal sealed record ResolveAlertsCommand(int NamespaceId, long JobId, long SourceEventId);
