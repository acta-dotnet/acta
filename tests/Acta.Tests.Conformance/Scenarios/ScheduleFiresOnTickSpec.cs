using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Schedules;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Deterministic <see cref="IActaClock"/> for the recurring specs. The C# walker reads this clock;
/// the SQL claim uses the real DB clock, so fake-past cursors stay claimable and fires are driven by
/// advancing this clock to the slot's due instant.
/// </summary>
internal sealed class FakeClock(DateTime initialUtc) : IActaClock
{
    private long _ticks = initialUtc.Ticks;

    public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) =>
        ValueTask.FromResult(new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc));

    public void AdvanceTo(DateTime utc) => Interlocked.Exchange(ref _ticks, utc.Ticks);
}

/// <summary>
/// Recurring-schedule conformance: a definition-sourced slot fires repeatedly on one stable Job id,
/// advances its per-schedule + slot cursors, surfaces the due set to the handler, trims its result
/// ring buffer, applies the failure budget, and emits the audit-gated lifecycle events.
/// </summary>
[ConformanceSpec(
    "schedule.fires-on-tick",
    "A recurring slot fires repeatedly on one stable id advancing cursors",
    Area = "Scheduling",
    Contract = "A recurring slot fires on one stable id, advancing cursors, trimming the result ring buffer, applying the failure budget and emitting rollover events.",
    Arrange = "A recurring-ping definition with an every-5-minutes schedule, MaxAttempts 2 and a result cap of 3 is registered under a fake clock.",
    Act = "The fake clock advances to each due instant and runtime ticks fire the slot repeatedly, including failing fires and a handler cancel.",
    Assert = "The slot fires repeatedly on one stable id, returning to Ready and advancing its cursors one period per fire."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.RegisterScheduledJobsAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetLiveSchedulesAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class ScheduleFiresOnTickSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
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

    [Fact(DisplayName = "One stable slot id fires repeatedly, returning to Ready and tracking execution_number")]
    public async Task Slot_fires_repeatedly_on_one_stable_id_returning_to_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        for (var i = 1; i <= 3; i++)
        {
            await FireOnceAsync(slotId, ct);
            Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(JobLookup.ById(slotId), ct));
        }

        // One stable slot row - no per-firing row inflation; execution_number tracked the fires.
        Assert.Equal(3, RecurringPingHandler.TriggersFor(TestNamespace).Count);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(3, slot!.ExecutionNumber);
    }

    [Fact(DisplayName = "Schedule and slot cursors advance one period and the slot tracks the MIN")]
    public async Task Schedule_and_slot_cursor_advance_and_track_the_min()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        var before = await ScheduleCursorAsync(slotId, ct);
        await FireOnceAsync(slotId, ct);
        var after = await ScheduleCursorAsync(slotId, ct);

        // Every-5-minute cadence: the schedule cursor rolled forward exactly one period.
        Assert.Equal(before.AddMinutes(5), after);

        // The slot's hot-path cursor tracks the MIN over its (single) live schedule.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(after, slot.NextRunAtUtc);
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
        Assert.Equal(new[] { ScheduleName }, triggers[0]);
    }

    [Fact(DisplayName = "Audit level emits started, finished and rolled-over events")]
    public async Task Audit_level_audit_emits_finished_and_rolled_over()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        await FireOnceAsync(slotId, ct);

        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        var codes = events.Select(e => e.JobEventCode).ToHashSet();
        Assert.Contains(JobEventCode.JobExecutionStarted, codes);
        Assert.Contains(JobEventCode.JobExecutionFinished, codes);
        Assert.Contains(JobEventCode.JobRecurringRolledOver, codes);
    }

    [Fact(DisplayName = "Result ring buffer trims to the cap keeping the newest entries")]
    public async Task Result_ring_buffer_trims_to_the_cap()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // RecurringPing declares RecurringResultCap = 3; fire 5 times and expect the newest 3 retained.
        for (var i = 0; i < 5; i++)
        {
            await FireOnceAsync(slotId, ct);
        }

        var resultCount = await Db.From<JobResult>().Where(r => r.JobId == slotId).CountAsync(ct);
        Assert.Equal(3, resultCount);

        var latest = await Services.GetRequiredService<IJobStore>().GetJobResultAsync(slotId, null, ct);
        Assert.NotNull(latest);
        Assert.Equal(5, latest!.ExecutionNumber);
        Assert.Equal(5, DeserializeResult(latest).Sequence);

        var fourth = await Services.GetRequiredService<IJobStore>().GetJobResultAsync(slotId, 4, ct);
        Assert.NotNull(fourth);
        Assert.Equal(4, fourth!.ExecutionNumber);
        Assert.Equal(4, DeserializeResult(fourth).Sequence);

        Assert.Null(await Services.GetRequiredService<IJobStore>().GetJobResultAsync(slotId, 1, ct));
    }

    [Fact(DisplayName = "In-budget failure re-arms Ready and a later success resets the failure count")]
    public async Task In_budget_failure_re_arms_ready_then_success_resets_the_count()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Fail the first fire only (MaxAttempts = 2, so one failure stays in budget).
        RecurringPingHandler.FailWhileSequenceAtMost[TestNamespace] = 1;
        await FireOnceAsync(slotId, ct);

        {
            var afterFailure = await ReadJobAsync(slotId, ct);
            Assert.Equal(JobStatusCode.Ready, afterFailure.Status);
            Assert.Equal((short)1, afterFailure.FailureCount);
        }

        // The next fire succeeds and resets the consecutive-failure count.
        await FireOnceAsync(slotId, ct);
        {
            var afterSuccess = await ReadJobAsync(slotId, ct);
            Assert.Equal(JobStatusCode.Ready, afterSuccess.Status);
            Assert.Equal((short)0, afterSuccess.FailureCount);
        }
    }

    [Fact(DisplayName = "Consecutive failures past MaxAttempts never terminalize a recurring slot")]
    public async Task Consecutive_failures_never_terminalize_the_recurring_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Fail every fire well past MaxAttempts = 2: a recurring slot re-arms Ready regardless of the
        // consecutive-failure count. MaxAttempts is the one-off retry budget only and never terminalizes a slot.
        RecurringPingHandler.FailWhileSequenceAtMost[TestNamespace] = 99;
        await FireOnceAsync(slotId, ct); // failure_count 1 -> Ready
        await FireOnceAsync(slotId, ct); // failure_count 2 -> Ready (would have been terminal under a budget)
        await FireOnceAsync(slotId, ct); // failure_count 3 -> Ready

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal((short)3, slot.FailureCount);
    }

    [Fact(DisplayName = "Handler cancel terminates the whole slot to Cancelled and stops the schedule")]
    public async Task Handler_cancel_terminates_the_whole_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // First fire rolls over as usual; the second fire's handler calls ctx.CancelAsync.
        await FireOnceAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(JobLookup.ById(slotId), ct));

        RecurringPingHandler.CancelOnSequence[TestNamespace] = 2;
        await FireOnceAsync(slotId, ct);

        // A deliberate handler cancel acts on the whole slot: terminal Cancelled, schedule stops.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Cancelled, slot.Status);
        Assert.Null(slot.NextRunAtUtc);

        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        var cancelEvent = Assert.Single(events.Where(e => e.JobEventCode == JobEventCode.JobCancelled));
        Assert.Equal(JobEventReasonCode.JobHandlerCancelled, cancelEvent.JobEventReasonCode);
    }

    // ---------- helpers ----------

    private async Task<long> SlotIdAsync(CancellationToken ct)
    {
        // The recurring slot's deduplication_key is the definition's job name.
        var id = await Jobs.ResolveJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, JobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    private async Task<DateTime> ScheduleCursorAsync(long slotId, CancellationToken ct)
    {
        var rows = await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct);
        var row = Assert.Single(rows);
        Assert.NotNull(row.NextRunAtUtc);
        return row.NextRunAtUtc!.Value;
    }

    private RecurringPingResult DeserializeResult(JobResultRecord row)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = JobPayload.FromBytes(row.Format, row.Data.ToArray());
        return serializers.Resolve(row.Format.Id).Deserialize<RecurringPingResult>(payload);
    }

    // Advance the fake clock to the slot's due instant so the walker sees the schedule due, then run
    // one tick. The SQL claim filter uses the (real) DB clock, against which the fake-derived cursor
    // is in the past, so the slot is always claimable.
    private async Task FireOnceAsync(long slotId, CancellationToken ct)
    {
        {
            var slot = await ReadJobAsync(slotId, ct);
            Assert.NotNull(slot!.NextRunAtUtc);
            Clock.AdvanceTo(slot.NextRunAtUtc!.Value);
        }

        var outcome = await Runtime.RunOnceAsync(slotId, ct);
        Assert.NotEqual(RunOnceOutcome.NothingClaimed, outcome);
    }
}
