namespace Acta;

/// <summary>One deliver-phase read: the namespace, how many due alerts to take, and the cursor the projector reached.</summary>
internal sealed record ListDeliverableAlertsQuery(int NamespaceId, int BatchSize, AlertsCursor Cursor);
