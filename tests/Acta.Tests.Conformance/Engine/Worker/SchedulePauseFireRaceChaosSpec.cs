using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The window between a firing being admitted and its attempt reaching the ledger. Settlement wakes the members
/// parked on the slot, so an operator pausing a schedule in between is racing a write that could put it back in
/// front of the admitter.
/// </summary>
/// <remarks>
/// Deterministic by construction. The pause runs inside the completion hook, so the interleaving is
/// this test's choice rather than whichever thread happened to win: a window normally microseconds
/// wide would otherwise make the spec a coin toss on every provider.
/// </remarks>
[ConformanceSpec(
    "schedule.pause-fire-race",
    "An operator pause landing inside a firing keeps the schedule paused",
    Area = "Scheduling",
    Contract = "A pause applied while a firing is in flight survives its completion: settlement never wakes a paused schedule."
)]
public sealed class SchedulePauseFireRaceChaosSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
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

    [Fact(DisplayName = "A pause applied while the firing is in flight is still in force after the completion")]
    public async Task Pause_inside_the_firing_window_survives_the_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Admit the firing at the schedule's occurrence: the cursor moves here, before the attempt runs.
        var due = await ScheduleAsync(Db, slotId, ct);
        Assert.NotNull(due.NextOccurrenceAtUtc);
        Clock.AdvanceTo(due.NextOccurrenceAtUtc!.Value);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        var admitted = await ScheduleAsync(Db, slotId, ct);

        // The pause lands while the attempt runs, before the completion settles the firing. An operator who pauses
        // rarely, and means it, must not have it undone by the settlement waking the slot's members.
        _faults.RunBeforeCompleteOnce(async () =>
        {
            var paused = await Schedules.PauseAsync(Lookup(), untilUtc: null, reasonMessage: "operator drain", ct: ct);
            Assert.Equal(ControlAction.Applied, paused.Action);
        });

        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));

        // The firing really ran: without this the pause could survive simply because nothing ran.
        Assert.Single(RecurringPingHandler.TriggersFor(TestNamespace));

        var after = await ScheduleAsync(Db, slotId, ct);
        Assert.Equal(ScheduleStatusCode.Paused, after.Status);
        Assert.Null(after.PausedUntilUtc);
        Assert.Null(after.NextEvaluationAtUtc);

        // The cursor stays where the admission left it: completion never computes an occurrence.
        Assert.Equal(admitted.NextOccurrenceAtUtc, after.NextOccurrenceAtUtc);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);

        // And the timeline must not claim an expiry. Nothing expired: an operator paused it.
        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        Assert.Contains(events, e => e.EventCode == EventCode.SchedulePaused);
        Assert.DoesNotContain(events, e => e.EventCode == EventCode.SchedulePauseExpired);
    }

    private ScheduleLookup Lookup() => new(TestNamespace, JobName, ScheduleName);

    private Task<long> SlotIdAsync(CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);

    private static async Task<ScheduleRow> ScheduleAsync(IDbSession session, long slotId, CancellationToken ct) =>
        Assert.Single(await ScheduleTestOps.ReadAsync(session, slotId, ct), s => s.ScheduleName == ScheduleName);
}
