namespace Acta;

/// <summary>The alerts due for delivery.</summary>
internal sealed record GetDeliverableAlertsResponse(IReadOnlyList<DeliverableAlert> Alerts);
