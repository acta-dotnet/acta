namespace Acta;

/// <summary>
/// The persisted decision state the evaluator reads: the next undecided nominal occurrence, the operator lifecycle
/// that gates admission, and the engine's own status, where <see cref="EvaluationStatusCode.Resumed"/> marks the first
/// evaluation after an operator's resume.
/// </summary>
internal sealed record ScheduleCursor(
    DateTime? NextOccurrenceAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    EvaluationStatusCode EvaluationStatus = EvaluationStatusCode.Scheduled
);
