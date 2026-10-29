using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// End-to-end firing behavior of schedule pause/resume on a live recurring slot: a paused schedule admits nothing,
/// and a timed pause ends when the admitter reaches its expiry, flipping the schedule back to Active, clearing the
/// pause, and emitting the pause-expired event. The end follows the misfire policy as of that instant: an occurrence
/// at or after it is admitted, one inside the window is skipped under Skip, and an end with nothing due admits nothing.
/// </summary>
[ConformanceSpec(
    "schedule.pause-firing",
    "A paused schedule admits nothing, and a timed pause ends by the misfire policy",
    Area = "Scheduling",
    Contract = "A paused schedule admits nothing, and a timed pause ends at its expiry by the misfire policy, admitting only an occurrence due by then."
)]
public sealed class SchedulePauseFiringSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private FakeClock Clock { get; set; } = null!;

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = new FakeClock(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "An indefinitely paused schedule admits nothing at its occurrence")]
    public async Task Indefinitely_paused_schedule_admits_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);
        var occurrence = await ScheduleCursorAsync(slotId, ct);

        var result = await Schedules.PauseAsync(Lookup(), ct: ct);
        Assert.Equal(ControlAction.Applied, result.Action);

        // The lone schedule is paused: the pass at its occurrence admits no firing, so a tick claims nothing.
        await AdmitAtAsync(occurrence, ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Empty(RecurringPingHandler.TriggersFor(TestNamespace));
    }

    [Fact(DisplayName = "A timed pause ends at its expiry, admits the occurrence due by then, returns to Active, and emits pause-expired")]
    public async Task Timed_pause_ends_and_admits_at_its_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        var until = await ScheduleCursorAsync(slotId, ct); // the schedule's next occurrence (fake-clock future)
        var paused = await Schedules.PauseAsync(Lookup(), untilUtc: until, ct: ct);
        Assert.Equal(ControlAction.Applied, paused.Action);

        // The admitter reaches the pause expiry: the pause ends, and the occurrence due at that instant is admitted.
        await AdmitAtAsync(until, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Single(RecurringPingHandler.TriggersFor(TestNamespace));

        {
            var schedule = await ScheduleAsync(Db, slotId, ct);
            Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
            Assert.Null(schedule.PausedUntilUtc);
        }

        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        Assert.Contains(EventCode.SchedulePauseExpired, events.Select(e => e.EventCode));
    }

    [Fact(DisplayName = "A timed pause whose window holds no occurrence ends at its expiry without admitting anything")]
    public async Task Timed_pause_with_nothing_due_ends_without_a_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        var occurrence = await ScheduleCursorAsync(slotId, ct);
        var until = occurrence.AddMinutes(-2);
        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(Lookup(), untilUtc: until, ct: ct)).Action);

        await AdmitAtAsync(until, ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Empty(RecurringPingHandler.TriggersFor(TestNamespace));
        var schedule = await ScheduleAsync(Db, slotId, ct);
        Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
        Assert.Null(schedule.PausedUntilUtc);
        Assert.Equal(occurrence, schedule.NextOccurrenceAtUtc);
        Assert.Equal(occurrence, schedule.NextEvaluationAtUtc);

        // The occurrence the pause never covered fires on time, once.
        await AdmitAtAsync(occurrence, ct);
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Single(RecurringPingHandler.TriggersFor(TestNamespace));
    }

    [Fact(DisplayName = "Under Skip an occurrence inside a timed pause is skipped at its expiry, not admitted late")]
    public async Task Timed_pause_skips_an_occurrence_inside_its_window()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        var missed = await ScheduleCursorAsync(slotId, ct);
        var until = missed.AddMinutes(2);
        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(Lookup(), untilUtc: until, ct: ct)).Action);

        await AdmitAtAsync(until, ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Empty(RecurringPingHandler.TriggersFor(TestNamespace));
        var next = missed.AddMinutes(5);
        Assert.Equal(next, (await ScheduleAsync(Db, slotId, ct)).NextOccurrenceAtUtc);

        await AdmitAtAsync(next, ct);
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Single(RecurringPingHandler.TriggersFor(TestNamespace));
    }

    private ScheduleLookup Lookup() => new(TestNamespace, JobName, ScheduleName);

    private Task<long> SlotIdAsync(CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);

    private async Task<DateTime> ScheduleCursorAsync(long slotId, CancellationToken ct)
    {
        var schedule = await ScheduleAsync(Db, slotId, ct);
        Assert.NotNull(schedule.NextOccurrenceAtUtc);
        return schedule.NextOccurrenceAtUtc!.Value;
    }

    private async Task AdmitAtAsync(DateTime nowUtc, CancellationToken ct)
    {
        Clock.AdvanceTo(nowUtc);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
    }

    private static async Task<ScheduleRow> ScheduleAsync(IDbSession session, long slotId, CancellationToken ct) =>
        Assert.Single(await ScheduleTestOps.ReadAsync(session, slotId, ct));
}
