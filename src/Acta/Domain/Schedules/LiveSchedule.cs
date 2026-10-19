namespace Acta;

/// <summary>
/// An effective schedule row read for one slot. Expression and time zone are the effective values
/// (override falling back to original). <c>NextRunAtUtc</c> is the stored cursor; null means the
/// schedule has no pending occurrence. <c>BaseExpression</c>/<c>BaseTimeZone</c> are the un-overridden
/// defaults, carried so a caller clearing an override (e.g. set-overrides) can compute the resulting
/// effective value without a second read. <c>Version</c> is the row version the read observed, which a
/// fire plan hands back so its advance is refused if the row moved in the meantime.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record LiveSchedule(
    string Name,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    DateTime? NextRunAtUtc,
    ScheduleStatusCode Status,
    DateTime? PausedUntilUtc,
    string BaseExpression,
    string BaseTimeZone,
    int Version
);
