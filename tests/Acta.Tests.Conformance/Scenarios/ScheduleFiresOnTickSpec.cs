using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Deterministic <see cref="IActaClock"/> for the recurring specs. The admitter reads this clock to find what is
/// due; the SQL claim uses the real DB clock, against which an admitted firing is due at once, so fires are driven
/// by advancing this clock to a schedule's occurrence and running one admission pass.
/// </summary>
internal sealed class FakeClock(DateTime initialUtc) : IActaClock
{
    private long _ticks = initialUtc.Ticks;

    public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) =>
        ValueTask.FromResult(new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc));

    public void AdvanceTo(DateTime utc) => Interlocked.Exchange(ref _ticks, utc.Ticks);
}

/// <summary>
/// Recurring-schedule conformance: a definition-sourced slot fires repeatedly on one stable Job id. The admitter
/// admits a due schedule's firing and advances its cursor, the attempt runs it with the triggering schedule in hand,
/// and the slot returns to Idle keeping its last result, spending one failure budget per firing, and emitting the
/// audit-gated lifecycle events.
/// </summary>
[ConformanceSpec(
    "schedule.fires-on-tick",
    "A permanent slot fires repeatedly on one stable id and returns to Idle",
    Area = "Scheduling",
    Contract = "A permanent slot fires on one stable id: admission advances the cursor, the attempt runs the firing, and the slot returns to Idle with one result per firing."
)]
public sealed class ScheduleFiresOnTickSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Register the deterministic clock BEFORE UseActa so its TryAddSingleton<IActaClock> no-ops.
        Clock = new FakeClock(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "One stable slot id fires repeatedly, returning to Idle and tracking execution_number")]
    public async Task Slot_fires_repeatedly_on_one_stable_id_returning_to_idle()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        for (var i = 1; i <= 3; i++)
        {
            await FireOnceAsync(slotId, ct);
            Assert.Equal(JobStatusCode.Idle, (await Jobs.GetStatusAsync(JobLookup.ById(slotId), ct))?.Status);
        }

        // One stable slot row - no per-firing row inflation; execution_number tracked the fires.
        Assert.Equal(3, RecurringPingHandler.TriggersFor(TestNamespace).Count);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(3, slot!.ExecutionNumber);
    }

    [Fact(DisplayName = "Admission advances the schedule's cursor one period, and the Idle slot carries no cursor of its own")]
    public async Task Admission_advances_the_schedule_cursor_one_period()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        var before = await ScheduleCursorAsync(slotId, ct);
        await FireOnceAsync(slotId, ct);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));

        // Every-5-minute cadence: the cursor rolled forward exactly one period past the admitted occurrence.
        Assert.Equal(before.AddMinutes(5), schedule.NextOccurrenceAtUtc);
        Assert.Equal(schedule.NextOccurrenceAtUtc, schedule.NextEvaluationAtUtc);

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
    }

    [Fact(DisplayName = "Handler sees the triggering schedule name in the due set")]
    public async Task Handler_sees_the_triggering_schedule_name()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        await FireOnceAsync(slotId, ct);

        var triggers = RecurringPingHandler.TriggersFor(TestNamespace);
        Assert.Single(triggers);
        Assert.Equal([ScheduleName], triggers[0]);
    }

    [Fact(DisplayName = "Audit recording emits the admission and the succeeded attempt")]
    public async Task Audit_recording_emits_admitted_and_succeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        await FireOnceAsync(slotId, ct);

        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        var codes = events.Select(e => e.EventCode).ToHashSet();
        Assert.Contains(EventCode.ScheduleAdmitted, codes);
        Assert.Contains(EventCode.JobExecutionSucceeded, codes);
    }

    [Fact(
        DisplayName = "The slot keeps its last successful firing's result, none while the next one runs, and none after a failed attempt"
    )]
    public async Task Slot_keeps_one_result_that_every_claim_clears()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);
        var slot = JobLookup.ById(slotId);

        await FireOnceAsync(slotId, ct);
        Assert.Equal(1, (await Jobs.GetResultAsync<RecurringPingResult>(slot, ct))?.Sequence);

        var observed = false;
        GetResultResponse? whileRunning = null;
        RecurringPingHandler.DuringNextFire[TestNamespace] = async () =>
        {
            whileRunning = await Jobs.GetResultAsync(slot, ct);
            observed = true;
        };
        await FireOnceAsync(slotId, ct);
        Assert.True(observed);
        Assert.Null(whileRunning);
        Assert.Equal(2, (await Jobs.GetResultAsync<RecurringPingResult>(slot, ct))?.Sequence);

        // A failed attempt re-arms the firing for its second try and keeps no result.
        RecurringPingHandler.FailWhileSequenceAtMost[TestNamespace] = 3;
        await FireOnceAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Null(await Jobs.GetResultAsync(slot, ct));
    }

    [Fact(
        DisplayName = "An in-budget failure re-arms the firing Ready, and its later success returns the slot to Idle with the count reset"
    )]
    public async Task In_budget_failure_re_arms_then_success_resets_the_count()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Fail the first attempt only (MaxAttempts = 2, so one failure stays in budget).
        RecurringPingHandler.FailWhileSequenceAtMost[TestNamespace] = 1;
        await FireOnceAsync(slotId, ct);

        {
            var afterFailure = await ReadJobAsync(slotId, ct);
            Assert.Equal(JobStatusCode.Ready, afterFailure.Status);
            Assert.Equal(1, afterFailure.FailureCount);
            Assert.Equal(CheckpointStatusCode.Pending, (await FiringAsync(slotId, ct)).Status);
        }

        // The firing's second attempt succeeds, settles it, and resets the count.
        await RetryNowAsync(slotId, ct);
        {
            var afterSuccess = await ReadJobAsync(slotId, ct);
            Assert.Equal(JobStatusCode.Idle, afterSuccess.Status);
            Assert.Equal(0, afterSuccess.FailureCount);
            Assert.Equal(CheckpointStatusCode.Succeeded, (await FiringAsync(slotId, ct)).Status);
        }
    }

    [Fact(
        DisplayName = "A firing that spends its budget returns the slot to Idle, never Failed, and the next firing starts a fresh budget"
    )]
    public async Task A_spent_budget_returns_the_slot_to_idle_and_the_next_firing_starts_afresh()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        RecurringPingHandler.FailWhileSequenceAtMost[TestNamespace] = 99;
        await FireOnceAsync(slotId, ct); // attempt 1 fails: re-armed
        await RetryNowAsync(slotId, ct); // attempt 2 fails: the budget of 2 is spent

        var spent = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, spent.Status);
        Assert.Equal(2, spent.FailureCount);
        Assert.Equal(CheckpointStatusCode.Failed, (await FiringAsync(slotId, ct)).Status);

        // The next admission zeroes the count: each firing spends its own budget.
        await FireOnceAsync(slotId, ct);
        var next = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, next.Status);
        Assert.Equal(1, next.FailureCount);
    }

    [Fact(DisplayName = "Handler cancel terminates the whole slot to Cancelled and settles the firing")]
    public async Task Handler_cancel_terminates_the_whole_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // The first firing settles as usual; the second firing's handler calls ctx.CancelAsync.
        await FireOnceAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, (await Jobs.GetStatusAsync(JobLookup.ById(slotId), ct))?.Status);

        RecurringPingHandler.CancelOnSequence[TestNamespace] = 2;
        await FireOnceAsync(slotId, ct);

        // A deliberate handler cancel acts on the whole slot: terminal Cancelled, the firing settled with it.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Cancelled, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
        Assert.Equal(CheckpointStatusCode.Cancelled, (await FiringAsync(slotId, ct)).Status);

        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        var cancelEvent = Assert.Single(events, e => e.EventCode == EventCode.JobCancelled);
        Assert.Equal(EventReason.HandlerCancelled, cancelEvent.Reason);
    }

    private Task<long> SlotIdAsync(CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);

    private async Task<DateTime> ScheduleCursorAsync(long slotId, CancellationToken ct)
    {
        var row = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.NotNull(row.NextOccurrenceAtUtc);
        return row.NextOccurrenceAtUtc!.Value;
    }

    private async Task<CheckpointEntity> FiringAsync(long slotId, CancellationToken ct) =>
        Assert.Single(
            await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing).ToListAsync(ct)
        );

    // Advances the fake clock to the schedule's next occurrence and runs one admission pass, which admits the firing
    // and makes the slot due on the DB clock, then runs the attempt.
    private async Task FireOnceAsync(long slotId, CancellationToken ct)
    {
        Clock.AdvanceTo(await ScheduleCursorAsync(slotId, ct));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
    }

    // A re-armed attempt waits out its backoff on the DB clock; this makes it due now and runs it.
    private async Task RetryNowAsync(long slotId, CancellationToken ct)
    {
        await ChaosSpecHelpers.SetReadyAsync(Db, slotId, ct);
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
    }
}
