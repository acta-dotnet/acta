namespace Acta;

/// <summary>
/// What decides a schedule's nominal occurrences: the expression, its kind, the zone a cron resolves in, and for an
/// interval the anchor its ticks step from, so a late evaluation never shifts the sequence. Everything the
/// <see cref="TimingFingerprint"/> covers, and nothing the operator can edit.
/// </summary>
internal sealed record ScheduleTiming(
    string Expression,
    ScheduleExpressionKindCode ExpressionKind,
    string? TimeZoneId,
    DateTime? IntervalAnchorAtUtc
);
