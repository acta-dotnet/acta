using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// End-to-end firing behavior of schedule pause/resume on a live recurring slot: a paused schedule's
/// slot is not claimable, and a timed pause auto-resumes when the scheduler reaches its expiry, firing
/// once, flipping the schedule back to Active, clearing the pause, and emitting the pause-expired event.
/// </summary>
[ConformanceSpec(
    "schedule.pause-firing",
    "A paused slot does not fire and a timed pause auto-resumes at its expiry",
    Area = "Scheduling",
    Contract = "A paused schedule's slot is not claimable, and a timed pause auto-resumes at its expiry firing once and clearing the pause.",
    Arrange = "A recurring-ping slot with a single every-5-minutes schedule is registered under a deterministic fake clock.",
    Act = "The schedule is paused indefinitely and then paused until an instant the advancing scheduler clock reaches.",
    Assert = "The paused slot yields NothingClaimed, and the timed pause auto-resumes at expiry firing once and clearing the pause."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.PauseScheduleAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetLiveSchedulesAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class SchedulePauseFiringSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
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

    [Fact(DisplayName = "An indefinitely paused schedule makes the slot yield NothingClaimed")]
    public async Task Indefinitely_paused_schedule_makes_the_slot_unclaimable()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        var result = await Schedules.PauseAsync(Lookup(), ct: ct);
        Assert.Equal(JobControlAction.Applied, result.Action);

        // The lone schedule paused -> the slot is system-paused, so a tick claims nothing.
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Empty(RecurringPingHandler.TriggersFor(TestNamespace));
    }

    [Fact(DisplayName = "A timed pause fires once at expiry, returns the schedule to Active with no pause window, and emits pause-expired")]
    public async Task Timed_pause_auto_resumes_and_fires_at_its_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        var until = await SlotNextRunAsync(slotId, ct); // the schedule's current due instant (fake-clock future)
        var paused = await Schedules.PauseAsync(Lookup(), untilUtc: until, ct: ct);
        Assert.Equal(JobControlAction.Applied, paused.Action);

        // The scheduler reaches the pause expiry: the slot wakes, the schedule auto-resumes and fires once.
        Clock.AdvanceTo(until);
        Assert.NotEqual(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Single(RecurringPingHandler.TriggersFor(TestNamespace));

        {
            var schedule = await ScheduleAsync(Db, slotId, ct);
            Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
            Assert.Null(schedule.PausedUntilUtc);
        }

        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        Assert.Contains(JobEventCode.SchedulePauseExpired, events.Select(e => e.JobEventCode));
    }

    // ---------- helpers ----------

    private JobScheduleLookup Lookup() => new(JobLookup.ByDeduplicationKey(TestNamespace, JobName), ScheduleName);

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

    private static async Task<JobSchedule> ScheduleAsync(IDbSession session, long slotId, CancellationToken ct)
    {
        var rows = await session.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct);
        return Assert.Single(rows);
    }
}
