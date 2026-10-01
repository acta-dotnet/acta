using Acta.Runtime.Modules.Execution.Schedules;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The startup reconcile in <see cref="ScheduleWalker.Reconcile"/> against stored state an operator or the
/// running fleet left behind: an override in force, an occurrence just due, an attempt in flight, an
/// occurrence parked part-way.
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
    public void A_cursor_due_less_than_a_minute_ago_stays_due_for_the_running_fleet()
    {
        var due = Now.AddSeconds(-30);
        var stored = Stored(due, ScheduleExpressionKindCode.Cron);

        var (schedules, status, nextRun) = ScheduleWalker.Reconcile([Cron("*/5 * * * *")], Map(stored), Now);

        Assert.Equal(due, Assert.Single(schedules).NextRunAtUtc);
        Assert.Equal(JobStatusCode.Ready, status);
        Assert.Equal(due, nextRun);
    }

    [Fact]
    public void An_attempt_in_flight_keeps_its_due_cursor_for_a_retry_to_see()
    {
        // A reclaim or a re-arm of this attempt plans from the cursor, so it must still name 12:00.
        var stored = InFlight(Utc(12, 0), armedAt: Utc(12, 0));

        var (schedules, _, _) = ScheduleWalker.Reconcile([CatchUp()], Map(stored), Now);

        Assert.Equal(Utc(12, 0), Assert.Single(schedules).NextRunAtUtc);
    }

    [Fact]
    public void A_start_that_edits_the_schedule_of_an_attempt_in_flight_moves_past_its_occurrence()
    {
        // The edit bumps the row's version and refuses the attempt's own advance, so the cursor moves here.
        var stored = InFlight(Utc(12, 0), armedAt: Utc(12, 0));

        var (schedules, _, _) = ScheduleWalker.Reconcile([CatchUp() with { Description = "edited" }], Map(stored), Now);

        Assert.Equal(Utc(12, 20), Assert.Single(schedules).NextRunAtUtc);
    }

    [Fact]
    public void An_edit_leaves_a_cursor_that_came_due_after_the_attempt_was_armed()
    {
        // Armed at 12:00 for another schedule; this one came due at 12:05, so the attempt never planned it.
        var stored = InFlight(Utc(12, 5), armedAt: Utc(12, 0));

        var (schedules, _, _) = ScheduleWalker.Reconcile([CatchUp() with { Description = "edited" }], Map(stored), Now);

        Assert.Equal(Utc(12, 5), Assert.Single(schedules).NextRunAtUtc);
    }

    private static StoredScheduleState InFlight(DateTime cursor, DateTime armedAt) =>
        Stored(cursor, ScheduleExpressionKindCode.Cron) with
        {
            SlotStatus = JobStatusCode.Executing,
            SlotNextRunAtUtc = armedAt,
            Expression = "*/5 * * * *",
            TimeZoneId = "UTC",
            MisfireStrategy = MisfireStrategyCode.CatchUpOnce,
        };

    private static ScheduleDescriptor CatchUp() => Cron("*/5 * * * *") with { MisfireStrategy = MisfireStrategyCode.CatchUpOnce };

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
