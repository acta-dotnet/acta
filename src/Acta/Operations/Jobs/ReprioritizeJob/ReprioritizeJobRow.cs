namespace Acta;

/// <summary>The (action, status_code, version) row a reprioritize answers.</summary>
[DbProjection]
internal sealed record ReprioritizeJobRow(ControlAction Action, JobStatusCode? Status, int? Version);
