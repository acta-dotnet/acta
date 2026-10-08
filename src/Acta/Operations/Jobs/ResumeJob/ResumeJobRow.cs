namespace Acta;

/// <summary>The (action, status_code, version) row a resume answers.</summary>
[DbProjection]
internal sealed record ResumeJobRow(ControlAction Action, JobStatusCode? Status, int? Version);
