namespace Acta;

/// <summary>
/// The row a resolve answers: the action plus the alert's <c>acknowledged_at_utc</c> / <c>resolved_at_utc</c>
/// after the attempt.
/// </summary>
[DbProjection]
internal sealed record ResolveAlertRow(ControlAction Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc);
