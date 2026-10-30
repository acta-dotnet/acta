namespace Acta;

/// <summary>
/// One schedule as registration writes it: the declared fields, capacity, expiry, and calendar names, the reconciled
/// cursor, and for an interval the anchor its ticks step from. The fingerprint is computed once from the timing fields,
/// so a declaration registers with the identity the evaluator will compare it against.
/// </summary>
internal sealed record SlotSchedule(
    string ScheduleName,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    string? Description,
    DateTime? NextOccurrenceAtUtc,
    DateTime? IntervalAnchorAtUtc = null,
    int MaxOutstanding = 1,
    ScheduleFullBehaviorCode FullBehavior = ScheduleFullBehaviorCode.Wait,
    int? ExpireAfterSeconds = null,
    ScheduleExpireActionCode? ExpireAction = null,
    string? AvailabilityCalendarName = null
)
{
    public string TimingFingerprint { get; } =
        Acta.TimingFingerprint.Compute(
            new ScheduleTiming(Expression, ExpressionKind, TimeZoneId, IntervalAnchorAtUtc, AvailabilityCalendarName)
        );
}
