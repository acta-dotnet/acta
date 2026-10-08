namespace Acta.Runtime;

/// <summary>The (action, status_code) row a purge answers; it carries no version.</summary>
[DbProjection]
internal sealed record PurgeJobRow(ControlAction Action, JobStatusCode? Status);
