namespace Acta;

/// <summary>
/// One evaluation's outcome, the whole of what the admitter writes for a schedule: the one nominal occurrence admitted
/// (null when none), the audited range the misfire policy skipped, the range that aged past the expiry, the new cursor
/// fields, whether an elapsed timed pause is over, and whether the schedule now pauses for an operator. Two decisions
/// with the same cursor fields, status, and nothing decided are the same, which is how a pass tells a no-op from a write.
/// </summary>
internal sealed record ScheduleEvaluation(
    AdmittedOccurrence? Admit,
    DateTime? SkippedFromUtc,
    DateTime? SkippedThroughUtc,
    DateTime? ExpiredFromUtc,
    DateTime? ExpiredThroughUtc,
    DateTime? NextOccurrenceAtUtc,
    DateTime? NextEvaluationAtUtc,
    EvaluationStatusCode Status,
    bool PauseEnded,
    bool PausedByExpiry
)
{
    /// <summary>Whether the evaluation changes anything the row holds, against the cursor it was made from.</summary>
    public bool Changes(ScheduleCursor cursor, EvaluationStatusCode status, DateTime? nextEvaluationAtUtc) =>
        Admit is not null
        || PauseEnded
        || PausedByExpiry
        || ExpiredFromUtc is not null
        || NextOccurrenceAtUtc != cursor.NextOccurrenceAtUtc
        || Status != status
        || NextEvaluationAtUtc != nextEvaluationAtUtc;
}

/// <summary>
/// The nominal occurrence an evaluation admitted, and, under a coalescing policy, the last due occurrence it stands
/// for: every occurrence in (<see cref="OccurrenceAtUtc"/>, <see cref="CoveredThroughUtc"/>] was decided by this one
/// firing. Equal when the firing stands for itself alone.
/// </summary>
internal sealed record AdmittedOccurrence(DateTime OccurrenceAtUtc, DateTime CoveredThroughUtc);
