using System.Runtime.CompilerServices;

namespace Acta;

/// <summary>
/// A workaround for zone data that states a zone with negative daylight time, as Ubuntu 26.04's states Europe/Dublin:
/// summer is the base, and winter is an hour of daylight time taken off it. .NET 10 misreports the hour such a zone's
/// clocks skip and the hour they repeat (dotnet/runtime#120915), and the cron library misplaces the repeated hour's
/// occurrences in such a zone even where those answers are right. A cron there stopped at the spring change, and a
/// calendar misplaced the skipped hour. Delete this file and its one call in
/// <see cref="NextOccurrenceCalculator.ResolveTimeZone"/> once its tests pass with that call gone.
/// </summary>
internal static class NegativeDaylightTime
{
    /// <summary>What each zone is computed in, since reading a zone's rules costs too much to repeat for each occurrence.</summary>
    private static readonly ConditionalWeakTable<TimeZoneInfo, TimeZoneInfo> Zones = new();

    /// <summary>
    /// The zone, or, when its present rule is a yearly one with negative daylight time, the same clock stated the
    /// ordinary way round: winter is the base and summer its daylight time, so the rule's two changes trade places.
    /// The rule stands for every year, so a year before this one is read by it, whatever dates the zone kept then.
    /// </summary>
    public static TimeZoneInfo Corrected(TimeZoneInfo zone) => Zones.GetValue(zone, Restate);

    private static TimeZoneInfo Restate(TimeZoneInfo zone)
    {
        if (
            zone.GetAdjustmentRules()
            is not [.., { DaylightDelta.Ticks: < 0, DateEnd.Year: 9999, DaylightTransitionStart.IsFixedDateRule: false } rule]
        )
        {
            return zone;
        }

        try
        {
            var restated = TimeZoneInfo.CreateCustomTimeZone(
                zone.Id,
                zone.BaseUtcOffset + rule.BaseUtcOffsetDelta + rule.DaylightDelta,
                zone.DisplayName,
                zone.DaylightName,
                zone.StandardName,
                [
                    TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                        DateTime.MinValue.Date,
                        DateTime.MaxValue.Date,
                        -rule.DaylightDelta,
                        rule.DaylightTransitionEnd,
                        rule.DaylightTransitionStart
                    ),
                ]
            );

            // The rule is restated for every year, and the zone's data may list the years up to the rule's own first
            // one by one, on other dates. The restated zone stands only if it keeps the zone's clock from this year on.
            var year = DateTime.UtcNow.Year;
            return KeepsTheClock(zone, restated, year, Math.Max(year, rule.DateStart.Year)) ? restated : zone;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidTimeZoneException)
        {
            // A rule that has no ordinary form, such as one whose daylight time is more than the fourteen hours a
            // zone may add, or whose years run to the last one a date can hold: the zone is left as it is.
            return zone;
        }
    }

    /// <summary>
    /// True when the two zones are one offset from UTC through the years: a day on which neither changes its offset is
    /// read once, and a day on which one does, minute by minute.
    /// </summary>
    private static bool KeepsTheClock(TimeZoneInfo zone, TimeZoneInfo restated, int fromYear, int toYear)
    {
        for (var day = new DateTime(fromYear, 1, 1, 0, 0, 0, DateTimeKind.Utc); day.Year <= toYear; day = day.AddDays(1))
        {
            var next = day.AddDays(1);
            var steady = zone.GetUtcOffset(day) == zone.GetUtcOffset(next) && restated.GetUtcOffset(day) == restated.GetUtcOffset(next);
            for (var at = day; at < next; at = steady ? next : at.AddMinutes(1))
            {
                if (zone.GetUtcOffset(at) != restated.GetUtcOffset(at))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
