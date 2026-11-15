namespace Acta;

/// <summary>The (action, status_code) row a clear answers; it carries no version.</summary>
[DbProjection]
internal sealed record ClearJobSignalRow(ControlAction Action, JobStatusCode? Status);
