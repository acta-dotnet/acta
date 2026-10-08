namespace Acta;

/// <summary>The job whose open failure alerts a success resolves, and that success event's id.</summary>
internal sealed record ResolveAlertsCommand(int NamespaceId, long JobId, long SourceEventId);
