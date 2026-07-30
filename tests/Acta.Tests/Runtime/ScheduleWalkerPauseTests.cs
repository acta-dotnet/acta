using System.Collections.Immutable;
using Acta.Modules.Execution.Schedules;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Pause-aware planning in <see cref="ScheduleWalker"/>: an indefinite pause is invisible to the slot,
/// a timed pause contributes only its expiry as the slot's wake point and auto-fires once elapsed, and
/// catalog reconcile preserves a paused schedule's stored cursor across a redeploy.
/// </summary>
public class ScheduleWalkerPauseTests
{
    private const string Cron5 = "*/5 * * * *";
    private static readonly DateTime Now = new(2024, 1, 1, 0, 0, 30, DateTimeKind.Utc);

    private static LiveSchedule Active(long id, string name, DateTime? cursor) =>
        new(
            id,
            name,
            Cron5,
            null,
            MisfireStrategyCode.Skip,
            ScheduleExpressionKindCode.Cron,
            cursor,
            ScheduleStatusCode.Active,
            null,
            Cron5,
            "UTC"
        );

    private static LiveSchedule Paused(long id, string name, DateTime? cursor, DateTime? pausedUntil) =>
        new(
            id,
            name,
            Cron5,
            null,
            MisfireStrategyCode.Skip,
            ScheduleExpressionKindCode.Cron,
            cursor,
            ScheduleStatusCode.Paused,
            pausedUntil,
            Cron5,
            "UTC"
        );

    [Fact]
    public void PlanFire_active_due_schedule_fires_and_advances()
    {
        var due = Now.AddMinutes(-1);
        var outcome = ScheduleWalker.PlanFire([Active(1, "a", due)], Now);

        Assert.Equal(new[] { "a" }, outcome.TriggeringScheduleNames);
        var advance = Assert.Single(outcome.Advances);
        Assert.NotNull(advance.NextRunAtUtc);
        Assert.True(advance.NextRunAtUtc > Now, "the advanced cursor must move strictly past now");
        Assert.Equal(advance.NextRunAtUtc, outcome.SlotMinNextRunAtUtc);
    }

    [Fact]
    public void PlanFire_indefinite_pause_never_fires_and_contributes_nothing()
    {
        var outcome = ScheduleWalker.PlanFire([Paused(1, "a", Now.AddMinutes(-10), null)], Now);

        Assert.Empty(outcome.TriggeringScheduleNames);
        Assert.Empty(outcome.Advances);
        Assert.Null(outcome.SlotMinNextRunAtUtc);
    }

    [Fact]
    public void PlanFire_timed_pause_ahead_is_not_due_but_sets_the_slot_wake_point()
    {
        var until = Now.AddMinutes(10);
        var outcome = ScheduleWalker.PlanFire([Paused(1, "a", Now.AddMinutes(-10), until)], Now);

        Assert.Empty(outcome.TriggeringScheduleNames);
        Assert.Equal(until, outcome.SlotMinNextRunAtUtc);
    }

    [Fact]
    public void PlanFire_elapsed_timed_pause_is_due_and_advances()
    {
        var outcome = ScheduleWalker.PlanFire([Paused(1, "a", Now.AddMinutes(-10), Now.AddMinutes(-1))], Now);

        Assert.Equal(new[] { "a" }, outcome.TriggeringScheduleNames);
        var advance = Assert.Single(outcome.Advances);
        Assert.True(advance.NextRunAtUtc > Now);
    }

    [Fact]
    public void PlanFire_slot_min_is_the_min_over_remaining_active_when_one_is_paused()
    {
        var soon = Now.AddMinutes(2);
        var outcome = ScheduleWalker.PlanFire([Active(1, "soon", soon), Paused(2, "off", Now.AddMinutes(-10), null)], Now);

        Assert.Empty(outcome.TriggeringScheduleNames);
        Assert.Equal(soon, outcome.SlotMinNextRunAtUtc);
    }

    [Fact]
    public void RecomputeSlotMin_active_reconciles_paused_contributes_pause_window()
    {
        var until = Now.AddMinutes(7);
        var min = ScheduleWalker.RecomputeSlotMin(
            [
                Active(1, "a", Now.AddMinutes(-10)),
                Paused(2, "timed", Now.AddMinutes(-10), until),
                Paused(3, "off", Now.AddMinutes(-10), null),
            ],
            Now
        );

        // Active reconciles to the next boundary after now (00:05:00), which is earlier than the timed
        // pause window (00:07:30); the indefinite pause contributes nothing.
        Assert.NotNull(min);
        Assert.True(min < until, "the reconciled active cursor outranks the timed-pause wake point here");
        Assert.True(min > Now);
    }

    [Fact]
    public void RecomputeSlotMin_is_null_when_only_indefinite_pauses_remain()
    {
        var min = ScheduleWalker.RecomputeSlotMin([Paused(1, "off", Now.AddMinutes(-10), null)], Now);
        Assert.Null(min);
    }

    [Fact]
    public void RecomputeSlotMin_timed_pause_is_the_wake_point_when_alone()
    {
        var until = Now.AddMinutes(3);
        var min = ScheduleWalker.RecomputeSlotMin([Paused(1, "timed", Now.AddMinutes(-10), until)], Now);
        Assert.Equal(until, min);
    }

    [Fact]
    public void Reconcile_preserves_a_paused_schedules_stored_cursor_across_redeploy()
    {
        var storedCursor = Now.AddMinutes(-37);
        var until = Now.AddMinutes(20);
        var declared = new[] { Descriptor("paused") };
        var stored = new Dictionary<string, StoredScheduleState>(StringComparer.Ordinal)
        {
            ["paused"] = new StoredScheduleState(1, "paused", storedCursor, ScheduleStatusCode.Paused, until),
        };

        var (schedules, slotMin) = ScheduleWalker.Reconcile(declared, stored, Now);

        var row = Assert.Single(schedules);
        Assert.Equal(storedCursor, row.NextRunAtUtc); // a paused schedule's remembered cursor is not advanced
        Assert.Equal(until, slotMin); // only the timed pause window contributes to the slot
    }

    [Fact]
    public void Reconcile_indefinite_pause_keeps_cursor_and_drops_out_of_the_slot()
    {
        var storedCursor = Now.AddMinutes(-37);
        var declared = new[] { Descriptor("off") };
        var stored = new Dictionary<string, StoredScheduleState>(StringComparer.Ordinal)
        {
            ["off"] = new StoredScheduleState(1, "off", storedCursor, ScheduleStatusCode.Paused, null),
        };

        var (schedules, slotMin) = ScheduleWalker.Reconcile(declared, stored, Now);

        Assert.Equal(storedCursor, Assert.Single(schedules).NextRunAtUtc);
        Assert.Null(slotMin);
    }

    [Fact]
    public void Reconcile_active_schedule_reconciles_and_contributes_its_cursor()
    {
        var declared = new[] { Descriptor("a") };
        var stored = new Dictionary<string, StoredScheduleState>(StringComparer.Ordinal)
        {
            ["a"] = new StoredScheduleState(1, "a", Now.AddMinutes(-37), ScheduleStatusCode.Active, null),
        };

        var (schedules, slotMin) = ScheduleWalker.Reconcile(declared, stored, Now);

        var row = Assert.Single(schedules);
        Assert.NotNull(row.NextRunAtUtc);
        Assert.True(row.NextRunAtUtc > Now, "an active schedule's missed cursor reconciles forward under Skip");
        Assert.Equal(row.NextRunAtUtc, slotMin);
    }

    private static JobScheduleDescriptor Descriptor(string scheduleName) =>
        new(
            "job",
            scheduleName,
            Cron5,
            null,
            MisfireStrategyCode.Skip,
            ScheduleExpressionKindCode.Cron,
            null,
            ImmutableArray<string>.Empty
        );
}
