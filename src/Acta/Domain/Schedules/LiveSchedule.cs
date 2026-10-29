namespace Acta;

/// <summary>
/// A live schedule row read for one slot. <c>NextOccurrenceAtUtc</c> is the stored cursor; null means the
/// schedule has no pending occurrence. <c>Version</c> is the row version the read observed, which a
/// fire plan hands back so its advance is refused if the row moved in the meantime.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record LiveSchedule(
    string ScheduleName,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    DateTime? NextOccurrenceAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    int Version
);
