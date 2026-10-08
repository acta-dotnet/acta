namespace Acta;

/// <summary>The (action, status_code, version) row a reschedule answers.</summary>
[DbProjection]
internal sealed record RescheduleJobRow(ControlAction Action, JobStatusCode? Status, int? Version);
