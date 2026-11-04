namespace Acta;

/// <summary>The alerts due for delivery.</summary>
internal sealed record ListDeliverableAlertsResponse(IReadOnlyList<DeliverableAlert> Alerts);
