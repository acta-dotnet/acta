namespace Acta;

/// <summary>
/// One schedule as registration writes it: the declared fields, the reconciled cursor, and for an interval the anchor
/// its ticks step from. The fingerprint is computed from the timing fields, so a declaration registers with the
/// identity the evaluator will compare it against.
/// </summary>
internal sealed record SlotSchedule(
    string ScheduleName,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    string? Description,
    DateTime? NextOccurrenceAtUtc,
    DateTime? IntervalAnchorAtUtc = null
)
{
    public string TimingFingerprint =>
        Acta.TimingFingerprint.Compute(new ScheduleTiming(Expression, ExpressionKind, TimeZoneId, IntervalAnchorAtUtc));
}
