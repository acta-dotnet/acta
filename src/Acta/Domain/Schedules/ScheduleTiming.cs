namespace Acta;

/// <summary>
/// What decides a schedule's nominal occurrences: the expression, its kind, the zone a cron or a calendar resolves in,
/// for an interval the anchor its ticks step from, so a late evaluation never shifts the sequence, and the calendars it
/// binds as resolved at registration, by origin, name, and version (the expression is the calendar's name when the
/// kind is Calendar). Everything the <see cref="TimingFingerprint"/> covers, and nothing the operator can edit.
/// </summary>
internal sealed record ScheduleTiming(
    string Expression,
    ScheduleExpressionKindCode ExpressionKind,
    string? TimeZoneId,
    DateTime? IntervalAnchorAtUtc,
    CalendarOriginCode? CalendarOrigin = null,
    int? CalendarVersion = null,
    string? AvailabilityCalendarName = null,
    CalendarOriginCode? AvailabilityOrigin = null,
    int? AvailabilityVersion = null
)
{
    /// <summary>The catalog versions this timing binds, by name: what must be compiled before the source is built.</summary>
    public IEnumerable<(string Name, int Version)> CatalogBindings()
    {
        if (
            this is
            { ExpressionKind: ScheduleExpressionKindCode.Calendar, CalendarOrigin: CalendarOriginCode.Catalog, CalendarVersion: { } v }
        )
        {
            yield return (Expression, v);
        }

        if (this is { AvailabilityCalendarName: { } name, AvailabilityOrigin: CalendarOriginCode.Catalog, AvailabilityVersion: { } av })
        {
            yield return (name, av);
        }
    }
}
