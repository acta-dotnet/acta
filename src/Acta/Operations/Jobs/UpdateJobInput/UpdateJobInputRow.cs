namespace Acta;

/// <summary>The (action, status_code) row an input amend answers; it carries no version.</summary>
[DbProjection]
internal sealed record UpdateJobInputRow(ControlAction Action, JobStatusCode? Status);
