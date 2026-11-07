namespace Acta;

/// <summary>
/// What decides a schedule's nominal occurrences: the expression, its kind, the zone a cron or a calendar resolves in,
/// for an interval the anchor its ticks step from, so a late evaluation never shifts the sequence, and the name of the
/// availability calendar it observes (the expression is the calendar's name when the kind is Calendar). A calendar is
/// bound by name only; the name resolves afresh at every evaluation. Everything the <see cref="TimingFingerprint"/>
/// covers, and nothing the operator can edit.
/// </summary>
internal sealed record ScheduleTiming(
    string Expression,
    ScheduleExpressionKindCode ExpressionKind,
    string? TimeZoneId,
    DateTime? IntervalAnchorAtUtc,
    string? AvailabilityCalendarName = null
)
{
    /// <summary>The calendar names this timing binds: what must be resolved before the source is built.</summary>
    public IEnumerable<string> CalendarNames()
    {
        if (ExpressionKind == ScheduleExpressionKindCode.Calendar)
        {
            yield return Expression;
        }

        if (AvailabilityCalendarName is { } name)
        {
            yield return name;
        }
    }
}
