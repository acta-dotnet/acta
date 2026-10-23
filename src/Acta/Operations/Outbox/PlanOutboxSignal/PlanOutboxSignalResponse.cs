namespace Acta;

/// <summary>Whether the plan was stored; false when the command read is no longer the one stored.</summary>
internal readonly record struct PlanOutboxSignalResponse(bool Planned);
