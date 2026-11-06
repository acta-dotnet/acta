namespace Acta;

/// <summary>The (action, status_code) row a raise answers; it carries no version.</summary>
[DbProjection]
internal sealed record RaiseJobSignalRow(ControlAction Action, JobStatusCode? Status);
