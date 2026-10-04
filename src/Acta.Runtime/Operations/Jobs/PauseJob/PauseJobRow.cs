namespace Acta.Runtime;

/// <summary>The pause routine's row: the outcome, and whether pausing the lane's running member promoted the next one.</summary>
[DbProjection]
internal sealed record PauseJobRow(ControlAction Action, JobStatusCode? Status, int? Version, bool LanePromoted);
