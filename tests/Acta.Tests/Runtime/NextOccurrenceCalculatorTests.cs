using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>Pure next-occurrence math: cron (5- and 6-field) and ISO 8601 or human intervals. No clock, no DB.</summary>
public sealed class NextOccurrenceCalculatorTests
{
    private static DateTime Utc(int h, int m, int s = 0) => new(2024, 1, 1, h, m, s, DateTimeKind.Utc);

    [Fact]
    public void Cron_5field_returns_next_boundary_strictly_after()
    {
        var next = NextOccurrenceCalculator.Next("*/5 * * * *", null, ScheduleExpressionKindCode.Cron, Utc(12, 0));
        Assert.Equal(Utc(12, 5), next);
    }

    [Fact]
    public void Cron_5field_skips_to_next_boundary_from_mid_interval()
    {
        var next = NextOccurrenceCalculator.Next("*/5 * * * *", null, ScheduleExpressionKindCode.Cron, Utc(12, 2, 30));
        Assert.Equal(Utc(12, 5), next);
    }

    [Fact]
    public void Cron_6field_seconds_supports_sub_minute()
    {
        var next = NextOccurrenceCalculator.Next("*/15 * * * * *", null, ScheduleExpressionKindCode.Cron, Utc(12, 0, 0));
        Assert.Equal(Utc(12, 0, 15), next);
    }

    [Fact]
    public void Unsatisfiable_cron_returns_null()
    {
        // Feb 30 never occurs.
        var next = NextOccurrenceCalculator.Next("0 0 30 2 *", null, ScheduleExpressionKindCode.Cron, Utc(12, 0));
        Assert.Null(next);
    }

    [Fact]
    public void Iso_interval_adds_duration()
    {
        var next = NextOccurrenceCalculator.Next("PT5M", null, ScheduleExpressionKindCode.Interval, Utc(12, 0));
        Assert.Equal(Utc(12, 5), next);
    }

    [Fact]
    public void Human_interval_adds_duration_like_its_iso_equivalent()
    {
        // The interval kind accepts the human duration syntax as well as ISO 8601; both forms of "5 minutes"
        // land on the same instant.
        var human = NextOccurrenceCalculator.Next("5m", null, ScheduleExpressionKindCode.Interval, Utc(12, 0));
        var iso = NextOccurrenceCalculator.Next("PT5M", null, ScheduleExpressionKindCode.Interval, Utc(12, 0));
        Assert.Equal(Utc(12, 5), human);
        Assert.Equal(iso, human);
    }

    [Fact]
    public void Calendar_iso_interval_still_parses()
    {
        // A calendar ISO 8601 duration (P1D) keeps working through the P-prefix path, unlike the restricted
        // policy-duration syntax which rejects calendar units.
        var next = NextOccurrenceCalculator.Next("P1D", null, ScheduleExpressionKindCode.Interval, Utc(12, 0));
        Assert.Equal(Utc(12, 0).AddDays(1), next);
    }

    [Fact]
    public void An_anchored_interval_steps_on_its_grid_from_any_instant()
    {
        // Anchor 12:00, 5-min interval, now 12:17 => first occurrence strictly after now = 12:20.
        var next = NextOccurrenceCalculator.FirstAfter("PT5M", null, ScheduleExpressionKindCode.Interval, Utc(12, 0), nowUtc: Utc(12, 17));
        Assert.Equal(Utc(12, 20), next);
    }

    [Fact]
    public void FirstAfter_iso_returns_anchor_when_anchor_is_future()
    {
        var anchor = Utc(13, 0);
        var next = NextOccurrenceCalculator.FirstAfter("PT5M", null, ScheduleExpressionKindCode.Interval, anchor, nowUtc: Utc(12, 0));
        Assert.Equal(anchor, next);
    }
}
