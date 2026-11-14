namespace Acta;

/// <summary>The (action, status_code, version) row an input amend answers.</summary>
[DbProjection]
internal sealed record UpdateJobInputRow(ControlAction Action, JobStatusCode? Status, long? Version);
