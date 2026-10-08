namespace Acta.Runtime;

/// <summary>
/// The row a resume answers: the action plus the schedule row's state after the attempt, one
/// <c>(action, status_code, paused_until_utc, next_run_at_utc, version)</c> row bound by position.
/// </summary>
[DbProjection]
internal sealed record ResumeScheduleRow(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextRunAtUtc,
    int? Version
);
