using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The window between a recurring fire being planned and that fire reaching the ledger. A slot reads
/// its live schedules once at claim and applies the resulting advances at completion, so an operator
/// pausing a schedule in between is racing a decision already made.
/// </summary>
/// <remarks>
/// Deterministic by construction. The pause runs inside the completion hook, so the interleaving is
/// this test's choice rather than whichever thread happened to win: a window normally microseconds
/// wide would otherwise make the spec a coin toss on every provider.
/// </remarks>
[ConformanceSpec(
    "schedule.pause-fire-race",
    "An operator pause landing inside a planned fire keeps the schedule paused",
    Area = "Scheduling",
    Contract = "A pause applied while a fire is in flight survives the completion, and only a timed pause that has elapsed is auto-resumed by an advance.",
    Arrange = "A due recurring-ping slot runs under a deterministic clock with the pause issued from inside the completion window.",
    Act = "The slot fires while an operator pauses its only schedule before the advance is written.",
    Assert = "The schedule stays Paused on its original cursor with no pause-expired event, and a separately elapsed timed pause still auto-resumes."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.PauseScheduleAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class SchedulePauseFireRaceChaosSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private FakeClock Clock { get; set; } = null!;
    private StoreFaultPlan _faults = null!;

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = new FakeClock(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
        _faults = services.AddStoreFaultInjection();
    }

    [Fact(DisplayName = "A pause applied while the fire is in flight is still in force after the completion")]
    public async Task Pause_inside_the_fire_window_survives_the_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Reach the schedule's due instant, or the planner finds nothing due and the completion carries
        // no advance at all - which would make this spec pass for the wrong reason.
        var due = await SlotNextRunAsync(slotId, ct);
        Clock.AdvanceTo(due);
        var cursorBefore = await ScheduleCursorAsync(slotId, ct);

        // The fire is planned from the pre-pause snapshot; the pause lands before the advance is
        // written. An operator who pauses rarely, and means it, must not have it undone by a fire that
        // was already decided.
        _faults.RunBeforeCompleteOnce(async () =>
        {
            var paused = await Schedules.PauseAsync(Lookup(), untilUtc: null, note: "operator drain", ct: ct);
            Assert.Equal(JobControlAction.Applied, paused.Action);
        });

        Assert.NotEqual(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));

        // The fire really happened: without this the pause could survive simply because nothing ran.
        Assert.Single(RecurringPingHandler.TriggersFor(TestNamespace));

        var after = await ScheduleAsync(Db, slotId, ct);
        Assert.Equal(ScheduleStatusCode.Paused, after.Status);
        Assert.Null(after.PausedUntilUtc);

        // The cursor does not move, which is what pausing always does: resume reconciles it by the
        // misfire policy rather than the fire silently consuming an occurrence.
        Assert.Equal(cursorBefore, after.NextRunAtUtc);

        // And the timeline must not claim an expiry. Nothing expired: an operator paused it.
        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        Assert.Contains(events, e => e.EventCode == EventCode.SchedulePaused);
        Assert.DoesNotContain(events, e => e.EventCode == EventCode.SchedulePauseExpired);
    }

    [Fact(DisplayName = "A timed pause that has elapsed is still auto-resumed by the advance")]
    public async Task Elapsed_timed_pause_is_still_auto_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // The guard must not break the case it was carved around: a pause whose expiry has passed is
        // due, is advanced, and returns to Active with the pause cleared.
        var until = await SlotNextRunAsync(slotId, ct);
        Assert.Equal(JobControlAction.Applied, (await Schedules.PauseAsync(Lookup(), until, note: "window", ct: ct)).Action);
        Clock.AdvanceTo(until);

        Assert.NotEqual(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));

        var after = await ScheduleAsync(Db, slotId, ct);
        Assert.Equal(ScheduleStatusCode.Active, after.Status);
        Assert.Null(after.PausedUntilUtc);

        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        Assert.Contains(events, e => e.EventCode == EventCode.SchedulePauseExpired);
    }

    private ScheduleLookup Lookup() => new(JobLookup.ByDeduplicationKey(TestNamespace, JobName), ScheduleName);

    private async Task<long> SlotIdAsync(CancellationToken ct)
    {
        var id = await Jobs.ResolveJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, JobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    private async Task<DateTime> SlotNextRunAsync(long slotId, CancellationToken ct)
    {
        var slot = await ReadJobAsync(slotId, ct);
        Assert.NotNull(slot!.NextRunAtUtc);
        return slot.NextRunAtUtc!.Value;
    }

    private async Task<DateTime?> ScheduleCursorAsync(long slotId, CancellationToken ct) =>
        (await ScheduleAsync(Db, slotId, ct)).NextRunAtUtc;

    private static async Task<JobSchedule> ScheduleAsync(IDbSession session, long slotId, CancellationToken ct)
    {
        var rows = await session.From<JobSchedule>().Where(s => s.JobId == slotId && s.Name == ScheduleName).ToListAsync(ct);
        return Assert.Single(rows);
    }
}
