namespace Acta;

/// <summary>
/// The row a resume answers: the action plus the schedule row's state after the attempt, one
/// <c>(action, status, paused_until_utc, next_occurrence_at_utc, version)</c> row bound by position.
/// </summary>
[DbProjection]
internal sealed record ResumeScheduleRow(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextOccurrenceAtUtc,
    int? Version
);
