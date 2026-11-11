using System.Xml;
using Cronos;

namespace Acta;

/// <summary>
/// Pure next-occurrence math for recurring schedules: every entry point takes an explicit UTC instant and answers exact
/// UTC instants. Cron is parsed with Cronos (6 fields enables seconds), an interval by <see cref="ParseInterval"/>, and a
/// calendar source asks its <see cref="IActaCalendar"/>; zones resolve through <see cref="ResolveTimeZone"/> for cron and
/// calendars, an interval is absolute. An availability calendar omits an occurrence outside its windows, never shifts it.
/// The timing's bounds hold every answer to [start, end) on the nominal instant; past the end the source is exhausted.
/// </summary>
internal static class NextOccurrenceCalculator
{
    /// <summary>The most occurrences one walk answers, whatever the caller asks.</summary>
    internal const int MaxWalk = 50;

    /// <summary>Closed windows one seek crosses before the availability calendar is judged broken rather than sparse.</summary>
    private const int MaxWindowHops = 10_000;

    /// <summary>
    /// Parses an interval expression to a <see cref="TimeSpan"/>: the human duration syntax (<c>5m</c>,
    /// <c>10s</c>) or ISO 8601 in days and smaller (<c>PT5M</c>, <c>P1D</c>). A <c>P</c>/<c>p</c> prefix routes to
    /// <see cref="XmlConvert.ToTimeSpan"/>; everything else is the human form. An interval is one fixed length, so a
    /// year or a month is refused. Whole milliseconds only: an occurrence is stored at millisecond precision on every
    /// provider, and a finer interval would make two ticks one stored instant.
    /// </summary>
    internal static TimeSpan ParseInterval(string expression)
    {
        var e = expression.TrimStart();
        var iso = e.Length > 0 && e[0] is 'P' or 'p';
        if (iso && DurationSyntax.IsoHasYearOrMonth(e))
        {
            throw new ArgumentException(
                $"Interval '{expression}' counts years or months, whose length varies. Write it in days, or use a cron.",
                nameof(expression)
            );
        }

        TimeSpan span;
        try
        {
            span = iso ? XmlConvert.ToTimeSpan(e) : DurationSyntax.ParseHuman(e);
        }
        catch (OverflowException)
        {
            throw new ArgumentException($"Interval '{expression}' is longer than a duration can hold.", nameof(expression));
        }
        return span.Ticks % TimeSpan.TicksPerMillisecond == 0
            ? span
            : throw new ArgumentException(
                $"Interval '{expression}' is finer than a millisecond, the precision an occurrence is stored at.",
                nameof(expression)
            );
    }

    /// <summary>
    /// Next occurrence strictly after <paramref name="afterUtc"/>. An interval steps on its anchor's grid, or from
    /// <paramref name="afterUtc"/> itself when the timing has no anchor. Null when the source is exhausted: an
    /// unsatisfiable cron (e.g. <c>0 0 30 2 *</c>), a non-positive interval, a calendar with no instant left, an
    /// availability calendar with no open window left, or nothing before the timing's end.
    /// </summary>
    public static DateTime? Next(ScheduleSource source, DateTime afterUtc)
    {
        var after = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        return FirstAfter(source, source.Timing.IntervalAnchorAtUtc ?? after, after);
    }

    /// <summary>The cron and interval form of <see cref="Next(ScheduleSource, DateTime)"/>, for a caller with no binding.</summary>
    public static DateTime? Next(string expression, string? timeZone, ScheduleExpressionKindCode kind, DateTime afterUtc) =>
        Next(ScheduleSource.Native(expression, timeZone, kind), afterUtc);

    /// <summary>
    /// First occurrence strictly after <paramref name="nowUtc"/>, anchored at a known prior occurrence
    /// <paramref name="anchorUtc"/>. Intervals step forward from the anchor in whole periods (drift-free; missed periods
    /// coalesce); cron and calendars ignore the anchor and seek from now.
    /// </summary>
    public static DateTime? FirstAfter(ScheduleSource source, DateTime anchorUtc, DateTime nowUtc)
    {
        source.Bound();
        var anchor = DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc);
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return Open(source, RawNext(source, now, anchor), anchor);
    }

    public static DateTime? FirstAfter(
        string expression,
        string? timeZone,
        ScheduleExpressionKindCode kind,
        DateTime anchorUtc,
        DateTime nowUtc
    ) => FirstAfter(ScheduleSource.Native(expression, timeZone, kind), anchorUtc, nowUtc);

    /// <summary>
    /// Up to <paramref name="count"/> (clamped to [1, <see cref="MaxWalk"/>]) occurrences strictly after <paramref name="fromUtc"/>, each
    /// computed from the previous, an interval on the grid of its anchor or of <paramref name="fromUtc"/>: since the
    /// seek is exclusive of its input, feeding occurrence N back in as the seed for N+1 cannot repeat it. Stops early
    /// (a shorter, possibly empty, list) once the source is exhausted.
    /// </summary>
    public static IReadOnlyList<DateTime> Walk(ScheduleSource source, DateTime fromUtc, int count)
    {
        var clamped = Math.Clamp(count, 1, MaxWalk);
        var results = new List<DateTime>(clamped);
        var current = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        var anchor = source.Timing.IntervalAnchorAtUtc ?? current;
        for (var i = 0; i < clamped; i++)
        {
            if (FirstAfter(source, anchor, current) is not { } next)
            {
                break;
            }

            results.Add(next);
            current = next;
        }

        return results;
    }

    public static IReadOnlyList<DateTime> Walk(
        string expression,
        string? timeZone,
        ScheduleExpressionKindCode kind,
        DateTime fromUtc,
        int count
    ) => Walk(ScheduleSource.Native(expression, timeZone, kind), fromUtc, count);

    // Parsed once per distinct expression: an evaluation steps a cron many times, and the expressions a process meets
    // are the registered declarations and what the operator previews, so the cache stays small.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CronExpression> Crons = new(StringComparer.Ordinal);

    internal static CronExpression ParseCron(string expression) =>
        Crons.GetOrAdd(
            expression,
            static e =>
            {
                var fieldCount = e.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                return CronExpression.Parse(e, fieldCount >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
            }
        );

    /// <summary>
    /// The bare source's first instant strictly after <paramref name="afterUtc"/> inside the timing's bounds, an interval
    /// on the grid of <paramref name="anchorUtc"/>: a seek from before the start seeks from the start, and a seek from or
    /// an answer at or after the end is null without asking the source. The bounds sit here rather than on the public
    /// answers because <see cref="Open"/> comes back here for the instant at each window's start, and a null ends that
    /// seek where an outer check would let it hop windows past the end up to its limit.
    /// </summary>
    private static DateTime? RawNext(ScheduleSource source, DateTime afterUtc, DateTime anchorUtc)
    {
        var timing = source.Timing;
        if (timing.EndBeforeUtc is { } end && afterUtc >= end)
        {
            return null;
        }

        if (timing.StartAtUtc is { } start && afterUtc < start)
        {
            afterUtc = start.AddTicks(-1);
        }

        var next = Bare(source, afterUtc, anchorUtc);
        return next >= timing.EndBeforeUtc ? null : next;
    }

    /// <summary>The source's first instant strictly after <paramref name="afterUtc"/> with no bound applied.</summary>
    private static DateTime? Bare(ScheduleSource source, DateTime afterUtc, DateTime anchorUtc)
    {
        var timing = source.Timing;
        switch (timing.ExpressionKind)
        {
            case ScheduleExpressionKindCode.Interval:
            {
                var interval = ParseInterval(timing.Expression);
                if (interval <= TimeSpan.Zero)
                {
                    return null;
                }

                if (anchorUtc > afterUtc)
                {
                    return anchorUtc;
                }

                return anchorUtc.AddTicks(interval.Ticks * ((afterUtc - anchorUtc).Ticks / interval.Ticks + 1));
            }
            case ScheduleExpressionKindCode.Cron:
                return ParseCron(timing.Expression).GetNextOccurrence(afterUtc, ResolveTimeZone(timing.TimeZoneId), inclusive: false);
            case ScheduleExpressionKindCode.Calendar:
                return CalendarNext(source, afterUtc);
            default:
                throw new NotSupportedException($"Schedule expression kind {timing.ExpressionKind} is not supported by this build.");
        }
    }

    /// <summary>The bare source's first instant at or after <paramref name="atUtc"/>: an occurrence exactly at a window's opening counts.</summary>
    private static DateTime? RawAtOrAfter(ScheduleSource source, DateTime atUtc, DateTime anchorUtc) =>
        RawNext(source, atUtc.AddTicks(-1), anchorUtc);

    /// <summary>The calendar's next instant, checked against its contract: UTC, advancing, and whole milliseconds.</summary>
    private static DateTime? CalendarNext(ScheduleSource source, DateTime afterUtc)
    {
        var name = source.Timing.Expression;
        DateTime? instant;
        try
        {
            instant = source.Calendar!.GetNextOccurrence(new CalendarContext(afterUtc, ResolveTimeZone(source.Timing.TimeZoneId)));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The calendar '{name}' threw while asked for the occurrence after {afterUtc:O}: {ex.Message}",
                ex
            );
        }

        if (instant is not { } next)
        {
            return null;
        }

        if (next.Kind != DateTimeKind.Utc)
        {
            throw new InvalidOperationException(
                $"The calendar '{name}' returned {next:O} of kind {next.Kind}; an occurrence is a UTC instant."
            );
        }

        if (next <= afterUtc)
        {
            throw new InvalidOperationException(
                $"The calendar '{name}' returned {next:O}, not after {afterUtc:O}; an occurrence must advance."
            );
        }

        if (next.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new InvalidOperationException(
                $"The calendar '{name}' returned {next:O}, finer than the millisecond an occurrence is stored at."
            );
        }

        return next;
    }

    /// <summary>
    /// The first occurrence from <paramref name="candidate"/> on that falls inside an availability window: a candidate
    /// in a closed range is dropped and the seek moves to the bare source's first instant at or after the next opening.
    /// </summary>
    private static DateTime? Open(ScheduleSource source, DateTime? candidate, DateTime anchorUtc)
    {
        if (source.Availability is not { } availability)
        {
            return candidate;
        }

        var name = source.Timing.AvailabilityCalendarName;
        var zone = ResolveTimeZone(source.Timing.TimeZoneId);
        for (var hops = 0; hops < MaxWindowHops; hops++)
        {
            if (candidate is not { } at)
            {
                return null;
            }

            CalendarWindow? window;
            try
            {
                window = availability.GetWindowAtOrAfter(new AvailabilityContext(at, zone));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The availability calendar '{name}' threw while asked for the window at or after {at:O}: {ex.Message}",
                    ex
                );
            }

            if (window is not { } w)
            {
                return null;
            }

            if (w.StartUtc.Kind != DateTimeKind.Utc || (w.EndUtc is { } end && (end.Kind != DateTimeKind.Utc || end <= w.StartUtc)))
            {
                throw new InvalidOperationException(
                    $"The availability calendar '{name}' returned the window [{w.StartUtc:O}, {w.EndUtc:O}); a window is UTC and ends after it starts."
                );
            }

            if (w.EndUtc <= at)
            {
                throw new InvalidOperationException(
                    $"The availability calendar '{name}' returned a window ending at {w.EndUtc:O} for {at:O}; the window must contain the instant or follow it."
                );
            }

            if (at >= w.StartUtc)
            {
                return at;
            }

            candidate = RawAtOrAfter(source, w.StartUtc, anchorUtc);
        }

        throw new InvalidOperationException(
            $"The availability calendar '{name}' closed {MaxWindowHops} windows in a row with no occurrence inside; the schedule cannot be evaluated."
        );
    }

    /// <summary>The zone a timing names, UTC when it names none.</summary>
    internal static TimeZoneInfo ResolveTimeZone(string? timeZone) =>
        string.IsNullOrWhiteSpace(timeZone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone);
}
