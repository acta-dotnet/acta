namespace Acta.Runtime;

/// <summary>The (action, status_code, version) row a restart answers.</summary>
[DbProjection]
internal sealed record RestartJobRow(ControlAction Action, JobStatusCode? Status, int? Version);
