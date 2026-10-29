namespace Acta;

/// <summary>
/// Local dates and times of a catalog definition resolved to UTC in a zone, with the two DST cases decided once for
/// every kind: a time inside a gap is the instant local time resumes, and an ambiguous time is its earlier instant.
/// A whole date runs from its local midnight to the next.
/// </summary>
internal static class ZonedTime
{
    public static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsAmbiguousTime(local))
        {
            return DateTime.SpecifyKind(local - zone.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);
        }

        if (!zone.IsInvalidTime(local))
        {
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }

        // The gap's transition instant: the first UTC instant that carries the offset in force after the gap, found by
        // bisection between the local time read with each offset.
        var utcAfter = DateTime.SpecifyKind(local.AddDays(1), DateTimeKind.Utc) - zone.BaseUtcOffset;
        var utcBefore = DateTime.SpecifyKind(local.AddDays(-1), DateTimeKind.Utc) - zone.BaseUtcOffset;
        var offsetAfter = zone.GetUtcOffset(utcAfter);
        var low = DateTime.SpecifyKind(local - offsetAfter, DateTimeKind.Utc);
        var high = DateTime.SpecifyKind(local - zone.GetUtcOffset(utcBefore), DateTimeKind.Utc);
        while (high - low > TimeSpan.FromTicks(1))
        {
            var mid = low.AddTicks((high - low).Ticks / 2);
            if (zone.GetUtcOffset(mid) == offsetAfter)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        return high;
    }

    /// <summary>The whole local date as a UTC range, its end the next date's start.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) Day(DateOnly date, TimeZoneInfo zone) =>
        (ToUtc(date, TimeOnly.MinValue, zone), ToUtc(date.AddDays(1), TimeOnly.MinValue, zone));

    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    /// <summary>The window's end on <paramref name="date"/>: a time at or before its start ends on the next date, and <c>24:00</c> is that midnight.</summary>
    public static DateTime WindowEndUtc(DateOnly date, TimeOnly start, TimeOnly end, bool endOfDay, TimeZoneInfo zone) =>
        endOfDay ? ToUtc(date.AddDays(1), TimeOnly.MinValue, zone)
        : end <= start ? ToUtc(date.AddDays(1), end, zone)
        : ToUtc(date, end, zone);
}
