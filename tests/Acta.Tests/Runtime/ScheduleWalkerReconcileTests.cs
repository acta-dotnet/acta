using Acta.Runtime.Modules.Execution.Schedules;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The startup reconcile in <see cref="ScheduleWalker.Reconcile"/> against stored state an operator or the
/// running fleet left behind: an override in force, an attempt in flight, an occurrence parked part-way.
/// </summary>
public class ScheduleWalkerReconcileTests
{
    private static DateTime Utc(int hour, int minute) => new(2024, 1, 1, hour, minute, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = Utc(12, 17);

    [Fact]
    public void A_missed_cursor_reconciles_under_the_operators_interval_override()
    {
        var stored = Stored(Utc(12, 0), ScheduleExpressionKindCode.Interval, expressionOverride: "PT1H");

        var (schedules, _, _) = ScheduleWalker.Reconcile([Interval("PT1M")], Map(stored), Now);

        // Under the declared minute the cursor would land at 12:18; the override in force says 13:00.
        var row = Assert.Single(schedules);
        Assert.Equal(Utc(13, 0), row.NextRunAtUtc);
        Assert.Equal("PT1M", row.Expression);
    }

    [Fact]
    public void A_missed_cursor_reconciles_under_the_operators_time_zone_override()
    {
        var stored = Stored(Utc(9, 0), ScheduleExpressionKindCode.Cron, timeZoneOverride: "Asia/Tokyo");

        var (schedules, _, _) = ScheduleWalker.Reconcile([Cron("0 22 * * *")], Map(stored), Now);

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

        var (schedules, _, _) = ScheduleWalker.Reconcile([Interval("PT5M")], Map(stored), Now);

        Assert.Equal(Utc(12, 20), Assert.Single(schedules).NextRunAtUtc);
    }

    [Fact]
    public void A_slot_suspended_part_way_keeps_its_state_and_its_due_cursor()
    {
        var stored = Stored(Utc(12, 0), ScheduleExpressionKindCode.Cron) with
        {
            SlotStatus = JobStatusCode.Suspended,
            SlotNextRunAtUtc = null,
        };

        var (schedules, status, nextRun) = ScheduleWalker.Reconcile([Cron("*/5 * * * *")], Map(stored), Now);

        Assert.Equal(Utc(12, 0), Assert.Single(schedules).NextRunAtUtc);
        Assert.Equal(JobStatusCode.Suspended, status);
        Assert.Null(nextRun);
    }

    [Fact]
    public void A_slot_re_armed_at_a_wake_instant_keeps_it_and_its_due_cursor()
    {
        // A step retry re-armed the 12:00 occurrence for 12:19; a re-arm never advances the cursor.
        var stored = Stored(Utc(12, 0), ScheduleExpressionKindCode.Cron) with
        {
            SlotNextRunAtUtc = Utc(12, 19),
        };

        var (schedules, status, nextRun) = ScheduleWalker.Reconcile([Cron("*/5 * * * *")], Map(stored), Now);

        Assert.Equal(Utc(12, 0), Assert.Single(schedules).NextRunAtUtc);
        Assert.Equal(JobStatusCode.Ready, status);
        Assert.Equal(Utc(12, 19), nextRun);
    }

    [Fact]
    public void An_idle_slot_due_long_ago_still_reconciles_by_its_misfire_policy()
    {
        var stored = Stored(Utc(12, 0), ScheduleExpressionKindCode.Cron);

        var (schedules, status, nextRun) = ScheduleWalker.Reconcile([Cron("*/5 * * * *")], Map(stored), Now);

        Assert.Equal(Utc(12, 20), Assert.Single(schedules).NextRunAtUtc);
        Assert.Equal(JobStatusCode.Ready, status);
        Assert.Equal(Utc(12, 20), nextRun);
    }

    [Fact]
    public void An_attempt_in_flight_has_consumed_a_catch_up_cursor()
    {
        var stored = Stored(Utc(12, 0), ScheduleExpressionKindCode.Cron) with { SlotStatus = JobStatusCode.Executing };
        var declared = Cron("*/5 * * * *") with { MisfireStrategy = MisfireStrategyCode.CatchUpOnce };

        var (schedules, _, _) = ScheduleWalker.Reconcile([declared], Map(stored), Now);

        // CatchUpOnce would keep 12:00 due; the running attempt is that catch-up, so the cursor moves past it.
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
