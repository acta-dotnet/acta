using Acta.Runtime.Modules.Execution.Schedules;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The startup reconcile in <see cref="ScheduleWalker.Reconcile"/> against stored state an operator or the
/// running fleet left behind: an override in force.
/// </summary>
public class ScheduleWalkerReconcileTests
{
    private static DateTime Utc(int hour, int minute) => new(2024, 1, 1, hour, minute, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = Utc(12, 17);

    [Fact]
    public void A_missed_cursor_reconciles_under_the_operators_interval_override()
    {
        var stored = Stored(Utc(12, 0), ScheduleExpressionKindCode.Interval, expressionOverride: "PT1H");

        var (schedules, _) = ScheduleWalker.Reconcile([Interval("PT1M")], Map(stored), Now);

        // Under the declared minute the cursor would land at 12:18; the override in force says 13:00.
        var row = Assert.Single(schedules);
        Assert.Equal(Utc(13, 0), row.NextRunAtUtc);
        Assert.Equal("PT1M", row.Expression);
    }

    [Fact]
    public void A_missed_cursor_reconciles_under_the_operators_time_zone_override()
    {
        var stored = Stored(Utc(9, 0), ScheduleExpressionKindCode.Cron, timeZoneOverride: "Asia/Tokyo");

        var (schedules, _) = ScheduleWalker.Reconcile([Cron("0 22 * * *")], Map(stored), Now);

        // 22:00 in Tokyo is 13:00 UTC; the declared UTC zone would say 22:00 UTC.
        var row = Assert.Single(schedules);
        Assert.Equal(Utc(13, 0), row.NextRunAtUtc);
        Assert.Equal("UTC", row.TimeZoneId);
    }

    [Fact]
    public void An_override_of_the_other_kind_yields_to_the_declaration()
    {
        // A deploy switched the schedule from cron to interval; the cron override no longer applies.
        var stored = Stored(Utc(12, 0), ScheduleExpressionKindCode.Cron, expressionOverride: "0 3 * * *");

        var (schedules, _) = ScheduleWalker.Reconcile([Interval("PT5M")], Map(stored), Now);

        Assert.Equal(Utc(12, 20), Assert.Single(schedules).NextRunAtUtc);
    }

    private static StoredScheduleState Stored(
        DateTime cursor,
        ScheduleExpressionKindCode kind,
        string? expressionOverride = null,
        string? timeZoneOverride = null
    ) => new(1, "tick", cursor, ScheduleStatusCode.Active, null, JobStatusCode.Ready, cursor, kind, expressionOverride, timeZoneOverride);

    private static Dictionary<string, StoredScheduleState> Map(params StoredScheduleState[] rows) =>
        rows.ToDictionary(r => r.ScheduleName, StringComparer.Ordinal);

    private static ScheduleDescriptor Interval(string expression) =>
        new("job", "tick", expression, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Interval, null, []);

    private static ScheduleDescriptor Cron(string expression) =>
        new("job", "tick", expression, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, []);
}
