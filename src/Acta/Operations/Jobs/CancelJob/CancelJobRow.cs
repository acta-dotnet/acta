namespace Acta;

/// <summary>
/// The cancel routine's row: the outcome, the parent whose child latch the caller raises after the cancel
/// commits, and whether the cancel promoted the next member of the job's lane.
/// </summary>
[DbProjection]
internal sealed record CancelJobRow(ControlAction Action, JobStatusCode? Status, long? ParentId, int? Version, bool LanePromoted);
