namespace Acta;

/// <summary>The (action, status_code) row a slot raise answers; it carries no version.</summary>
[DbProjection]
internal sealed record RaiseSignalRow(ControlAction Action, JobStatusCode? Status);
