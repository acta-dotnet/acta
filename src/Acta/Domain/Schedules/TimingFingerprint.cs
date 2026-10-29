using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta;

/// <summary>
/// The identity of a schedule's timing: a hash over everything that decides its occurrences, the expression, its
/// kind, the zone and that zone's rules, an interval's anchor, and the calendars it binds. Two declarations with the
/// same fingerprint produce the same occurrences, so registration leaves the cursor alone; a changed fingerprint is a
/// timing change.
/// </summary>
internal static class TimingFingerprint
{
    /// <summary>Bumped whenever the evaluator's interpretation of an unchanged declaration changes.</summary>
    private const string EvaluatorVersion = "1";

    public static string Compute(ScheduleTiming timing)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder(EvaluatorVersion)
            .Append('|')
            .Append(((byte)timing.ExpressionKind).ToString(c))
            .Append('|')
            .Append(timing.Expression)
            .Append('|')
            .Append(timing.IntervalAnchorAtUtc?.Ticks.ToString(c))
            .Append('|')
            .Append(timing.CalendarOrigin is { } calendarOrigin ? ((byte)calendarOrigin).ToString(c) : null)
            .Append('|')
            .Append(timing.CalendarVersion?.ToString(c))
            .Append('|')
            .Append(timing.AvailabilityCalendarName)
            .Append('|')
            .Append(timing.AvailabilityOrigin is { } availabilityOrigin ? ((byte)availabilityOrigin).ToString(c) : null)
            .Append('|')
            .Append(timing.AvailabilityVersion?.ToString(c))
            .Append('|');
        // A cron, a calendar, and an availability window resolve in the zone, and the zone's rules are part of what they
        // mean; a bare interval is absolute.
        if (timing.ExpressionKind != ScheduleExpressionKindCode.Interval || timing.AvailabilityCalendarName is not null)
        {
            var zone = string.IsNullOrWhiteSpace(timing.TimeZoneId)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(timing.TimeZoneId);
            text.Append(zone.Id).Append('|').Append(zone.BaseUtcOffset.Ticks.ToString(c));
            foreach (var rule in zone.GetAdjustmentRules())
            {
                text.Append('|')
                    .Append(rule.DateStart.Ticks.ToString(c))
                    .Append(',')
                    .Append(rule.DateEnd.Ticks.ToString(c))
                    .Append(',')
                    .Append(rule.DaylightDelta.Ticks.ToString(c))
                    .Append(',')
                    .Append(Transition(rule.DaylightTransitionStart))
                    .Append(',')
                    .Append(Transition(rule.DaylightTransitionEnd));
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Transition(TimeZoneInfo.TransitionTime t) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{t.TimeOfDay.Ticks}/{t.Month}/{t.Week}/{t.Day}/{(int)t.DayOfWeek}/{(t.IsFixedDateRule ? 1 : 0)}"
        );
}
