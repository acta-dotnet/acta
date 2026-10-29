namespace Acta;

/// <summary>
/// The persisted decision state the evaluator reads: the next undecided nominal occurrence, the watermark every
/// earlier occurrence was decided through, and the operator lifecycle that gates admission.
/// </summary>
internal sealed record ScheduleCursor(
    DateTime? NextOccurrenceAtUtc,
    DateTime? DecidedThroughAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc
);
