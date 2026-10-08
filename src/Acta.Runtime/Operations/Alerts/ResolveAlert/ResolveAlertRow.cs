namespace Acta.Runtime;

/// <summary>
/// The row a resolve answers: the action plus the alert's <c>acknowledged_at_utc</c> / <c>resolved_at_utc</c>
/// after the attempt. Bound by position: the PostgreSQL routines name their columns with an <c>out_</c> prefix.
/// </summary>
[DbProjection]
internal sealed record ResolveAlertRow(ControlAction Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc);
