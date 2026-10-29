namespace Acta;

/// <summary>
/// One evaluation's outcome, the whole of what the admitter writes for a schedule. <see cref="Admit"/> is the one
/// nominal occurrence selected, null when none is; <see cref="SkippedFromUtc"/> through
/// <see cref="SkippedThroughUtc"/> is the audited range of occurrences discarded without work; the cursor fields are
/// the new persisted values; <see cref="PauseEnded"/> says an elapsed timed pause is over. Two decisions with the same
/// cursor fields, status, and no admission are the same, which is how a pass tells a no-op from a write.
/// </summary>
internal sealed record ScheduleEvaluation(
    AdmittedOccurrence? Admit,
    DateTime? SkippedFromUtc,
    DateTime? SkippedThroughUtc,
    DateTime? NextOccurrenceAtUtc,
    DateTime? DecidedThroughAtUtc,
    DateTime? NextEvaluationAtUtc,
    EvaluationStatusCode Status,
    bool PauseEnded
)
{
    /// <summary>Whether the evaluation changes anything the row holds, against the cursor it was made from.</summary>
    public bool Changes(ScheduleCursor cursor, EvaluationStatusCode status, DateTime? nextEvaluationAtUtc) =>
        Admit is not null
        || PauseEnded
        || NextOccurrenceAtUtc != cursor.NextOccurrenceAtUtc
        || DecidedThroughAtUtc != cursor.DecidedThroughAtUtc
        || Status != status
        || NextEvaluationAtUtc != nextEvaluationAtUtc;
}

/// <summary>
/// The nominal occurrence an evaluation admitted, and, under a coalescing policy, the last due occurrence it stands
/// for: every occurrence in (<see cref="OccurrenceAtUtc"/>, <see cref="CoveredThroughUtc"/>] was decided by this one
/// firing. Equal when the firing stands for itself alone.
/// </summary>
internal sealed record AdmittedOccurrence(DateTime OccurrenceAtUtc, DateTime CoveredThroughUtc);
