namespace Acta;

/// <summary>One deliver-phase read: the namespace and how many due alerts to take.</summary>
internal sealed record GetDeliverableAlertsQuery(int NamespaceId, int BatchSize);
