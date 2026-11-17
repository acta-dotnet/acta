namespace Acta;

/// <summary>The (action, status_code, version) row an input update answers.</summary>
[DbProjection]
internal sealed record UpdateJobInputRow(ControlAction Action, JobStatusCode? Status, long? Version);
