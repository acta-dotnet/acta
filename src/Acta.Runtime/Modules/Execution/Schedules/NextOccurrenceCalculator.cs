using System.Xml;
using Cronos;

namespace Acta.Runtime.Modules.Execution.Schedules;

/// <summary>
/// Pure next-occurrence math for recurring schedules. No ambient clock; every entry point takes an
/// explicit UTC instant and returns exact UTC instants. Cron is parsed with Cronos (6 fields enables
/// seconds); interval durations are parsed by <see cref="ParseInterval"/> - human (e.g. <c>5m</c>) or
/// ISO 8601 (e.g. <c>PT5M</c>, <c>P1D</c>). Time zones resolve via
/// <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> (cron only; intervals are absolute).
/// </summary>
internal static class NextOccurrenceCalculator
{
    /// <summary>
    /// Parses an interval expression to a <see cref="TimeSpan"/>: the human duration syntax (<c>5m</c>,
    /// <c>10s</c>) or full ISO 8601 (<c>PT5M</c>, and calendar forms such as <c>P1D</c>). A <c>P</c>/<c>p</c>
    /// prefix routes to <see cref="XmlConvert.ToTimeSpan"/> (so calendar durations keep working); everything
    /// else is the human form.
    /// </summary>
    internal static TimeSpan ParseInterval(string expression)
    {
        var e = expression.TrimStart();
        return e.Length > 0 && e[0] is 'P' or 'p' ? XmlConvert.ToTimeSpan(e) : DurationSyntax.ParseHuman(e);
    }

    /// <summary>
    /// Next occurrence strictly after <paramref name="afterUtc"/>. For intervals this is
    /// <paramref name="afterUtc"/> + the duration. Null when a cron expression is unsatisfiable
    /// (e.g. <c>0 0 30 2 *</c>) or an interval is non-positive.
    /// </summary>
    public static DateTime? Next(string expression, string? timeZone, ScheduleExpressionKindCode kind, DateTime afterUtc)
    {
        var after = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        if (kind == ScheduleExpressionKindCode.Interval)
        {
            var interval = ParseInterval(expression);
            return interval <= TimeSpan.Zero ? null : after + interval;
        }

        return ParseCron(expression).GetNextOccurrence(after, ResolveTimeZone(timeZone), inclusive: false);
    }

    /// <summary>
    /// First occurrence strictly after <paramref name="nowUtc"/>, anchored at a known prior
    /// occurrence <paramref name="anchorUtc"/>. Intervals step forward from the anchor in whole
    /// periods (drift-free; missed periods coalesce); cron ignores the anchor and seeks from now.
    /// </summary>
    public static DateTime? FirstAfter(
        string expression,
        string? timeZone,
        ScheduleExpressionKindCode kind,
        DateTime anchorUtc,
        DateTime nowUtc
    )
    {
        if (kind != ScheduleExpressionKindCode.Interval)
        {
            return Next(expression, timeZone, kind, nowUtc);
        }

        var interval = ParseInterval(expression);
        if (interval <= TimeSpan.Zero)
        {
            return null;
        }

        var anchor = DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc);
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        if (anchor > now)
        {
            return anchor;
        }

        var steps = (long)Math.Floor((now - anchor) / interval) + 1;
        return anchor.AddTicks(interval.Ticks * steps);
    }

    /// <summary>
    /// MisfireStrategy-aware reconciliation for startup / resume / restart. New schedules (null stored
    /// cursor) seed from the first occurrence after now. An un-missed stored cursor (still ahead of
    /// now) is kept. A missed cursor either fires once now (<see cref="MisfireStrategyCode.CatchUpOnce"/>,
    /// keep the past instant so the next fire coalesces all misses) or skips to the first occurrence
    /// after now (<see cref="MisfireStrategyCode.Skip"/>).
    /// </summary>
    public static DateTime? Reconcile(
        string expression,
        string? timeZone,
        ScheduleExpressionKindCode kind,
        MisfireStrategyCode misfire,
        DateTime? storedNextRunUtc,
        DateTime nowUtc
    )
    {
        if (storedNextRunUtc is not { } stored)
        {
            return Next(expression, timeZone, kind, nowUtc);
        }

        stored = DateTime.SpecifyKind(stored, DateTimeKind.Utc);
        return stored > DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) ? stored
            : misfire == MisfireStrategyCode.CatchUpOnce ? stored
            : FirstAfter(expression, timeZone, kind, stored, nowUtc);
    }

    /// <summary>
    /// Up to <paramref name="count"/> (clamped to [1, 50]) occurrences strictly after <paramref name="fromUtc"/>,
    /// each computed from the previous: since <see cref="Next"/> is already exclusive of its input, feeding
    /// occurrence N back in as the seed for occurrence N+1 cannot repeat it, so no tick/second fixup is
    /// needed at the seam. Stops early (a shorter, possibly empty, list) once the expression is exhausted.
    /// </summary>
    public static IReadOnlyList<DateTime> Walk(
        string expression,
        string? timeZone,
        ScheduleExpressionKindCode kind,
        DateTime fromUtc,
        int count
    )
    {
        var clamped = Math.Clamp(count, 1, 50);
        var results = new List<DateTime>(clamped);
        var current = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        for (var i = 0; i < clamped; i++)
        {
            if (Next(expression, timeZone, kind, current) is not { } next)
            {
                break;
            }

            results.Add(next);
            current = next;
        }

        return results;
    }

    internal static CronExpression ParseCron(string expression)
    {
        var fieldCount = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return CronExpression.Parse(expression, fieldCount >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZone) =>
        string.IsNullOrWhiteSpace(timeZone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZone);
}
