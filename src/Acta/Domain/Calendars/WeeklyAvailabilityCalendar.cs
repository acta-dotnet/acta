namespace Acta;

/// <summary>
/// The <c>weekly-availability</c> kind: weekly rules give each weekday its local windows, a date override replaces
/// one date's windows (an empty list closes it, and cuts an overnight window reaching into it at its midnight), and
/// excluded ranges are subtracted last. Windows are produced date by date in the zone, merged when they touch, and the
/// first one ending after the asked instant is answered.
/// </summary>
internal sealed class WeeklyAvailabilityCalendar(
    TimeZoneInfo? fixedZone,
    IReadOnlyDictionary<DayOfWeek, IReadOnlyList<LocalWindow>> weekly,
    IReadOnlyDictionary<DateOnly, IReadOnlyList<LocalWindow>> dateOverrides,
    IReadOnlyList<ExcludedRange> excludedRanges
) : IActaAvailabilityCalendar
{
    /// <summary>Dates a weekly rule is followed before the exclusions are judged to close the calendar for good.</summary>
    internal const int MaxScanDays = 1100;

    // The exclusions resolve in the zone once; a walk asks for window after window in the same zone.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        List<(DateTime StartUtc, DateTime EndUtc)>
    > _exclusionsByZone = new(StringComparer.Ordinal);

    public CalendarWindow? GetWindowAtOrAfter(AvailabilityContext context)
    {
        var zone = fixedZone ?? context.TimeZone;
        var at = DateTime.SpecifyKind(context.AtOrAfterUtc, DateTimeKind.Utc);
        var exclusions = _exclusionsByZone.GetOrAdd(
            zone.Id,
            static (_, state) => state.Ranges.Select(r => r.Resolve(state.Zone)).OrderBy(r => r.StartUtc).ToList(),
            (Ranges: excludedRanges, Zone: zone)
        );
        CalendarWindow? pending = null;
        foreach (var date in Dates(ZonedTime.LocalDate(at, zone).AddDays(-1)))
        {
            foreach (var window in WindowsOn(date, zone))
            {
                foreach (var open in Subtract(window, exclusions))
                {
                    if (pending is { } p && open.StartUtc <= p.EndUtc)
                    {
                        pending = new CalendarWindow(p.StartUtc, open.EndUtc > p.EndUtc ? open.EndUtc : p.EndUtc);
                        continue;
                    }

                    if (pending is { } done && done.EndUtc > at)
                    {
                        return done;
                    }

                    pending = open;
                }
            }
        }

        if (pending is { } last && last.EndUtc > at)
        {
            return last;
        }

        return weekly.Count == 0
            ? null
            : throw new InvalidOperationException(
                $"No open window within {MaxScanDays} days of {at:O}; the exclusions close the calendar."
            );
    }

    /// <summary>The dates to visit: every date for a weekly calendar, only the override dates for one without weekly rules.</summary>
    private IEnumerable<DateOnly> Dates(DateOnly from)
    {
        if (weekly.Count > 0)
        {
            for (var i = 0; i < MaxScanDays; i++)
            {
                yield return from.AddDays(i);
            }

            yield break;
        }

        foreach (var date in dateOverrides.Keys.Where(d => d >= from).Order())
        {
            yield return date;
        }
    }

    private IEnumerable<CalendarWindow> WindowsOn(DateOnly date, TimeZoneInfo zone)
    {
        var overridden = dateOverrides.TryGetValue(date, out var o);
        var windows = overridden ? o! : weekly.GetValueOrDefault(date.DayOfWeek) ?? [];
        var nextOverridden = dateOverrides.ContainsKey(date.AddDays(1));
        foreach (var w in windows.OrderBy(w => w.Start))
        {
            var start = ZonedTime.ToUtc(date, w.Start, zone);
            var end = ZonedTime.WindowEndUtc(date, w.Start, w.End, w.EndOfDay, zone);
            // An overnight window's portion on the next date yields to that date's override.
            if (nextOverridden && (w.EndOfDay || w.End <= w.Start))
            {
                end = ZonedTime.ToUtc(date.AddDays(1), TimeOnly.MinValue, zone);
            }

            if (end > start)
            {
                yield return new CalendarWindow(start, end);
            }
        }
    }

    private static IEnumerable<CalendarWindow> Subtract(CalendarWindow window, List<(DateTime StartUtc, DateTime EndUtc)> exclusions)
    {
        var start = window.StartUtc;
        var end = window.EndUtc!.Value;
        foreach (var (exclusionStart, exclusionEnd) in exclusions)
        {
            if (exclusionEnd <= start)
            {
                continue;
            }

            if (exclusionStart >= end)
            {
                break;
            }

            if (exclusionStart > start)
            {
                yield return new CalendarWindow(start, exclusionStart);
            }

            start = exclusionEnd;
            if (start >= end)
            {
                yield break;
            }
        }

        yield return new CalendarWindow(start, end);
    }
}

/// <summary>A local window of a weekly rule or a date override: <c>[Start, End)</c>, overnight when the end is at or before the start, <c>24:00</c> as the end of the date.</summary>
internal readonly record struct LocalWindow(TimeOnly Start, TimeOnly End, bool EndOfDay);

/// <summary>A closed range: whole local dates, inclusive, or two UTC instants.</summary>
internal readonly record struct ExcludedRange(DateOnly? FromDate, DateOnly? ToDate, DateTime? FromInstant, DateTime? ToInstant)
{
    public (DateTime StartUtc, DateTime EndUtc) Resolve(TimeZoneInfo zone) =>
        FromDate is { } from
            ? (ZonedTime.Day(from, zone).StartUtc, ZonedTime.Day(ToDate!.Value, zone).EndUtc)
            : (FromInstant!.Value, ToInstant!.Value);
}
