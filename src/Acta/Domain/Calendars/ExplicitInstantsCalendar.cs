using System.Collections.Concurrent;

namespace Acta;

/// <summary>
/// The <c>explicit-instants</c> kind: a fixed list of instants, each a UTC literal or a local date-time resolved in
/// the zone, sorted once per zone. Exhausted after the last.
/// </summary>
internal sealed class ExplicitInstantsCalendar(
    TimeZoneInfo? fixedZone,
    IReadOnlyList<DateTime> utcInstants,
    IReadOnlyList<DateTime> localInstants
) : IActaCalendar
{
    private readonly ConcurrentDictionary<string, DateTime[]> _byZone = new(StringComparer.Ordinal);

    public DateTime? GetNextOccurrence(CalendarContext context)
    {
        var zone = fixedZone ?? context.TimeZone;
        var instants = _byZone.GetOrAdd(zone.Id, _ => Resolve(zone));
        var index = Array.BinarySearch(instants, DateTime.SpecifyKind(context.AfterUtc, DateTimeKind.Utc));
        index = index < 0 ? ~index : index + 1;
        return index < instants.Length ? instants[index] : null;
    }

    private DateTime[] Resolve(TimeZoneInfo zone) =>
        [
            .. utcInstants
                .Concat(localInstants.Select(l => ZonedTime.ToUtc(DateOnly.FromDateTime(l), TimeOnly.FromDateTime(l), zone)))
                .Distinct()
                .Order(),
        ];
}

/// <summary>
/// The <c>daily-local-time</c> kind: the listed local times on the listed weekdays, minus the excluded dates, resolved
/// in the zone date by date; a DST gap or fold decides a time as every kind does.
/// </summary>
internal sealed class DailyLocalTimeCalendar(
    TimeZoneInfo? fixedZone,
    IReadOnlyList<TimeOnly> times,
    IReadOnlySet<DayOfWeek> days,
    IReadOnlySet<DateOnly> excludedDates
) : IActaCalendar
{
    /// <summary>Dates visited before the exclusions are judged to have closed the source; a weekday rule hits inside a week.</summary>
    private const int MaxScanDays = 1100;

    public DateTime? GetNextOccurrence(CalendarContext context)
    {
        var zone = fixedZone ?? context.TimeZone;
        var after = DateTime.SpecifyKind(context.AfterUtc, DateTimeKind.Utc);
        var date = ZonedTime.LocalDate(after, zone).AddDays(-1);
        for (var i = 0; i < MaxScanDays; i++, date = date.AddDays(1))
        {
            if (!days.Contains(date.DayOfWeek) || excludedDates.Contains(date))
            {
                continue;
            }

            // The day's instants are sorted after resolution: a gap can reorder two times that straddle it.
            var next = times.Select(t => ZonedTime.ToUtc(date, t, zone)).Where(u => u > after).Order().Cast<DateTime?>().FirstOrDefault();
            if (next is not null)
            {
                return next;
            }
        }

        throw new InvalidOperationException($"No occurrence within {MaxScanDays} days of {after:O}; the excluded dates close the source.");
    }
}

/// <summary>
/// The <c>business-day</c> kind: the nth first or last eligible date of each week, month, quarter, or year, over the
/// source's own business weekdays and holidays, at a local time. A period with fewer eligible dates than <c>nth</c>
/// has no occurrence. Infinite, so the search stops at a bound only when every period is empty.
/// </summary>
internal sealed class BusinessDayCalendar(
    TimeZoneInfo? fixedZone,
    bool last,
    BusinessPeriod period,
    int nth,
    IReadOnlySet<DayOfWeek> businessDays,
    IReadOnlySet<DateOnly> holidays,
    TimeOnly time
) : IActaCalendar
{
    private const int MaxPeriods = 1000;

    public DateTime? GetNextOccurrence(CalendarContext context)
    {
        var zone = fixedZone ?? context.TimeZone;
        var after = DateTime.SpecifyKind(context.AfterUtc, DateTimeKind.Utc);
        var start = PeriodStart(ZonedTime.LocalDate(after, zone));
        for (var i = 0; i < MaxPeriods; i++)
        {
            var end = PeriodEnd(start);
            var eligible = Eligible(start, end);
            var ordered = last ? Enumerable.Reverse(eligible) : eligible;
            var chosen = ordered.Skip(nth - 1).Cast<DateOnly?>().FirstOrDefault();
            if (chosen is { } date && ZonedTime.ToUtc(date, time, zone) is var utc && utc > after)
            {
                return utc;
            }

            start = end.AddDays(1);
        }

        throw new InvalidOperationException($"No business day within {MaxPeriods} periods of {after:O}; the holidays close the source.");
    }

    private List<DateOnly> Eligible(DateOnly start, DateOnly end)
    {
        var dates = new List<DateOnly>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (businessDays.Contains(d.DayOfWeek) && !holidays.Contains(d))
            {
                dates.Add(d);
            }
        }

        return dates;
    }

    private DateOnly PeriodStart(DateOnly date) =>
        period switch
        {
            BusinessPeriod.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
            BusinessPeriod.Month => new DateOnly(date.Year, date.Month, 1),
            BusinessPeriod.Quarter => new DateOnly(date.Year, ((date.Month - 1) / 3 * 3) + 1, 1),
            _ => new DateOnly(date.Year, 1, 1),
        };

    private DateOnly PeriodEnd(DateOnly start) =>
        period switch
        {
            BusinessPeriod.Week => start.AddDays(6),
            BusinessPeriod.Month => start.AddMonths(1).AddDays(-1),
            BusinessPeriod.Quarter => start.AddMonths(3).AddDays(-1),
            _ => start.AddYears(1).AddDays(-1),
        };
}

/// <summary>The period a <c>business-day</c> source selects within; a week starts on Monday.</summary>
internal enum BusinessPeriod
{
    Week,
    Month,
    Quarter,
    Year,
}

/// <summary>The <c>at-opening</c> kind: an occurrence at the start of each window of an inline availability calendar.</summary>
internal sealed class AtOpeningCalendar(IActaAvailabilityCalendar availability) : IActaCalendar
{
    public DateTime? GetNextOccurrence(CalendarContext context)
    {
        var after = DateTime.SpecifyKind(context.AfterUtc, DateTimeKind.Utc);
        if (availability.GetWindowAtOrAfter(new AvailabilityContext(after, context.TimeZone)) is not { } window)
        {
            return null;
        }

        if (window.StartUtc > after)
        {
            return window.StartUtc;
        }

        // The window contains the instant: the next opening is the window after it.
        if (window.EndUtc is not { } end)
        {
            return null;
        }

        return availability.GetWindowAtOrAfter(new AvailabilityContext(end, context.TimeZone))?.StartUtc;
    }
}

/// <summary>The <c>union</c> of occurrence sources: the earliest next occurrence among them.</summary>
internal sealed class UnionCalendar(IReadOnlyList<IActaCalendar> sources) : IActaCalendar
{
    public DateTime? GetNextOccurrence(CalendarContext context)
    {
        DateTime? earliest = null;
        foreach (var source in sources)
        {
            if (source.GetNextOccurrence(context) is { } next && (earliest is null || next < earliest))
            {
                earliest = next;
            }
        }

        return earliest;
    }
}
