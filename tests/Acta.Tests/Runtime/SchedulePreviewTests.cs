using Acta.Features.Schedules;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Pure next-run forecasting via <see cref="NextOccurrenceCalculator.Walk"/>, the engine behind
/// <see cref="ISchedules.PreviewAsync"/>, with no DB and no ambient clock: a zoned cron walk keeps
/// its wall-clock meaning across a DST spring-forward boundary, an ISO interval walk is evenly
/// spaced, the requested count is clamped to [1, 50], and an unsatisfiable expression yields no
/// occurrences at all.
/// </summary>
public sealed class SchedulePreviewTests
{
    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Cron_walk_yields_ten_correct_instants_across_the_spring_forward_boundary()
    {
        // "0 9 * * *" in Europe/Ljubljana: 09:00 local is 08:00Z under CET (UTC+1, through 2026-03-28)
        // and 07:00Z under CEST (UTC+2, from 2026-03-29, the last Sunday of March, the documented
        // Ljubljana spring-forward switch). The walk crosses that boundary: instant 5 -> 6 advances by
        // only 23 hours instead of the usual 24, which is the DST evidence this test is pinned on.
        var runs = NextOccurrenceCalculator.Walk("0 9 * * *", "Europe/Ljubljana", ScheduleExpressionKindCode.Cron, Utc(2026, 3, 24), 10);

        Assert.Equal(
            [
                Utc(2026, 3, 24, 8),
                Utc(2026, 3, 25, 8),
                Utc(2026, 3, 26, 8),
                Utc(2026, 3, 27, 8),
                Utc(2026, 3, 28, 8),
                Utc(2026, 3, 29, 7), // spring-forward: local 09:00 is now only 2 hours ahead of UTC
                Utc(2026, 3, 30, 7),
                Utc(2026, 3, 31, 7),
                Utc(2026, 4, 1, 7),
                Utc(2026, 4, 2, 7),
            ],
            runs
        );
    }

    [Fact]
    public void Iso_interval_walk_is_evenly_spaced()
    {
        var start = Utc(2026, 1, 1);
        var runs = NextOccurrenceCalculator.Walk("PT1H", null, ScheduleExpressionKindCode.Interval, start, 5);

        Assert.Equal([start.AddHours(1), start.AddHours(2), start.AddHours(3), start.AddHours(4), start.AddHours(5)], runs);
    }

    [Fact]
    public void Count_clamps_at_fifty_and_floors_at_one()
    {
        var start = Utc(2026, 1, 1);

        var tooMany = NextOccurrenceCalculator.Walk("PT1M", null, ScheduleExpressionKindCode.Interval, start, 500);
        var tooFew = NextOccurrenceCalculator.Walk("PT1M", null, ScheduleExpressionKindCode.Interval, start, 0);
        var negative = NextOccurrenceCalculator.Walk("PT1M", null, ScheduleExpressionKindCode.Interval, start, -5);

        Assert.Equal(50, tooMany.Count);
        Assert.Equal(start.AddMinutes(50), tooMany[^1]);
        Assert.Single(tooFew);
        Assert.Single(negative);
    }

    [Fact]
    public void Exhausted_expression_yields_an_empty_list()
    {
        // "0 0 30 2 *" (Feb 30) can never be satisfied: the same canonical unsatisfiable example
        // documented on NextOccurrenceCalculator.Next.
        var runs = NextOccurrenceCalculator.Walk("0 0 30 2 *", null, ScheduleExpressionKindCode.Cron, Utc(2026, 1, 1), 10);

        Assert.Empty(runs);
    }
}
