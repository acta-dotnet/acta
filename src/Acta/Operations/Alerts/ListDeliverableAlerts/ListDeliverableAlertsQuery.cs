namespace Acta;

/// <summary>One deliver-phase read: the namespace and how many due alerts to take.</summary>
internal sealed record ListDeliverableAlertsQuery(int NamespaceId, int BatchSize);
