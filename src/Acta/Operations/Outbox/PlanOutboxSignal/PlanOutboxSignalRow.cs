namespace Acta;

/// <summary>The plan outcome projection: how many rows the rewrite changed (0 or 1).</summary>
[DbProjection]
internal sealed record PlanOutboxSignalRow(long Planned);
