using Acta.Features.Schedules;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Timezone-aware cron math, no DB: a zoned schedule keeps its wall-clock meaning across DST
/// transitions (pinned to the documented Europe/Ljubljana switches on 2026-03-29 and 2026-10-25),
/// a fire inside the skipped hour shifts to the first valid instant, a fire inside the ambiguous
/// hour happens once, Windows ids resolve like IANA ids, and an unknown id throws.
/// </summary>
public sealed class NextOccurrenceCalculatorTimeZoneTests
{
    private const string DailyAt8 = "0 8 * * *";
    private const string SkippedHour = "30 2 * * *";
    private const string Ljubljana = "Europe/Ljubljana";

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static DateTime? NextCron(string expression, string? timeZone, DateTime afterUtc) =>
        NextOccurrenceCalculator.Next(expression, timeZone, ScheduleExpressionKindCode.Cron, afterUtc);

    [Fact]
    public void Daily_cron_keeps_wall_clock_across_both_dst_offsets()
    {
        // Winter (CET, UTC+1): 08:00 local is 07:00Z.
        Assert.Equal(Utc(2026, 1, 15, 7), NextCron(DailyAt8, Ljubljana, Utc(2026, 1, 15)));

        // Summer (CEST, UTC+2): 08:00 local is 06:00Z.
        Assert.Equal(Utc(2026, 7, 15, 6), NextCron(DailyAt8, Ljubljana, Utc(2026, 7, 15)));

        // Across the spring transition: Saturday fires at 07:00Z, Sunday (post-switch) at 06:00Z.
        Assert.Equal(Utc(2026, 3, 28, 7), NextCron(DailyAt8, Ljubljana, Utc(2026, 3, 28)));
        Assert.Equal(Utc(2026, 3, 29, 6), NextCron(DailyAt8, Ljubljana, Utc(2026, 3, 28, 8)));
    }

    [Fact]
    public void Fire_inside_the_skipped_spring_forward_hour_shifts_to_the_first_valid_instant()
    {
        // 2026-03-29 02:30 local does not exist (02:00 CET jumps to 03:00 CEST). The fire shifts to
        // the first valid instant after the gap: 03:00 CEST = 01:00Z.
        Assert.Equal(Utc(2026, 3, 29, 1), NextCron(SkippedHour, Ljubljana, Utc(2026, 3, 29)));
    }

    [Fact]
    public void Fire_inside_the_ambiguous_fall_back_hour_happens_once()
    {
        // 2026-10-25 02:30 local occurs twice (00:30Z as CEST, 01:30Z as CET). The fire happens at
        // the first (daylight) instance only; the next occurrence is the following day.
        var first = NextCron(SkippedHour, Ljubljana, Utc(2026, 10, 24, 23));
        Assert.Equal(Utc(2026, 10, 25, 0, 30), first);

        Assert.Equal(Utc(2026, 10, 26, 1, 30), NextCron(SkippedHour, Ljubljana, first!.Value));
    }

    [Fact]
    public void Windows_id_resolves_like_the_matching_iana_id()
    {
        Assert.Equal(
            NextCron(DailyAt8, Ljubljana, Utc(2026, 1, 15)),
            NextCron(DailyAt8, "Central European Standard Time", Utc(2026, 1, 15))
        );
    }

    [Fact]
    public void Null_time_zone_means_utc()
    {
        Assert.Equal(Utc(2026, 1, 15, 8), NextCron(DailyAt8, null, Utc(2026, 1, 15)));
    }

    [Fact]
    public void Unknown_time_zone_id_throws()
    {
        Assert.Throws<TimeZoneNotFoundException>(() => NextCron(DailyAt8, "Mars/Olympus-Mons", Utc(2026, 1, 15)));
    }
}
