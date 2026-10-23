using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Signals;

/// <summary>Conformance for the engine's wait decision on one slot: arm and suspend, continue on Set, or time out once the stored expiry passes.</summary>
[ConformanceSpec(
    "wait-signal.decision",
    "A wait arms its slot once and answers suspend, continue, or timed out",
    Area = "Signals",
    Contract = "Wait arms a Pending slot once, continues with a Set slot's payload, and answers TimedOut past a stored expiry or at once for a slot first armed past an instant."
)]
public sealed class WaitSignalSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private SignalsOperations Slots => Services.GetRequiredService<SignalsOperations>();

    // The wait of the execution EnqueueAsync stages, bounded by a length or by an instant.
    private Task<WaitSignalResponse> WaitAsync(long jobId, string name, int? timeoutSeconds, CancellationToken ct) =>
        Slots.WaitSignalAsync(Staged(jobId), CheckpointKindCode.Signal, name, timeoutSeconds, dueAtUtc: null, ct);

    private Task<WaitSignalResponse> WaitUntilAsync(long jobId, string name, DateTime dueAtUtc, CancellationToken ct) =>
        Slots.WaitSignalAsync(Staged(jobId), CheckpointKindCode.Signal, name, timeoutSeconds: null, dueAtUtc, ct);

    private static ExecutionFence Staged(long jobId) => new(jobId, 1, JobStateStaging.StagedWorkerId);

    // DB now cut to whole seconds, so the instant a test arms survives every provider's stored precision unchanged.
    private async Task<DateTime> DbNowAsync(CancellationToken ct)
    {
        var now = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private async Task<CheckpointEntity> ReadSlotAsync(long jobId, CheckpointKindCode kind, string name, CancellationToken ct) =>
        Assert.Single(
            await Db.From<CheckpointEntity>().Where(c => c.JobId == jobId && c.Kind == kind && c.CheckpointName == name).ToListAsync(ct)
        );

    // A job staged Executing, as a claim leaves it, so the wait passes the execution fence.
    private async Task<long> EnqueueAsync(CancellationToken ct) =>
        (
            await ExecutionFenceStaging.ExecutingAsync(
                Db,
                (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct)).JobId,
                ct
            )
        ).JobId;

    [Fact(DisplayName = "A first wait arms a Pending slot and answers SuspendPending without touching the job")]
    public async Task First_wait_arms_a_pending_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var before = await ReadJobAsync(jobId, ct);

        var decision = await WaitAsync(jobId, "go", null, ct);

        Assert.Equal(WaitSignalOutcomeCode.SuspendPending, decision.Outcome);
        Assert.Null(decision.Value);
        var slot = await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct);
        Assert.Equal(CheckpointStatusCode.Pending, slot.Status);
        Assert.Null(slot.DueAtUtc);

        // The decision is the caller's to act on: the wait itself never moves the job.
        var after = await ReadJobAsync(jobId, ct);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact(DisplayName = "A repeat wait while Pending answers SuspendPending and leaves the slot untouched")]
    public async Task Repeat_wait_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        await WaitAsync(jobId, "go", null, ct);
        var armed = await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct);

        Assert.Equal(WaitSignalOutcomeCode.SuspendPending, (await WaitAsync(jobId, "go", null, ct)).Outcome);

        var slot = await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct);
        Assert.Equal(armed.Version, slot.Version);
        Assert.Equal(CheckpointStatusCode.Pending, slot.Status);
    }

    [Fact(DisplayName = "A wait on a Set slot continues with the stored payload")]
    public async Task Set_slot_continues_with_its_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var payload = JobPayload.Json(new ReviewDecision(true, "approved"));
        await Jobs.RaiseSignalAsync(JobLookup.ById(jobId), "review", payload, ct: ct);

        var decision = await WaitAsync(jobId, "review", 60, ct);

        Assert.Equal(WaitSignalOutcomeCode.ContinueSet, decision.Outcome);
        Assert.Equal(payload.Format.Id, decision.ValueFormatId);
        Assert.Equal(payload.Data.ToArray(), decision.Value);
    }

    [Fact(DisplayName = "A bounded wait stores its expiry once, and a later wait neither moves nor clears it")]
    public async Task Expiry_is_armed_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var clock = Services.GetRequiredService<IActaClock>();

        var start = await clock.GetUtcNowAsync(ct);
        await WaitAsync(jobId, "go", 60, ct);
        var end = await clock.GetUtcNowAsync(ct);
        var due = Assert.IsType<DateTime>((await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct)).DueAtUtc);
        Assert.InRange(due, start.AddSeconds(59), end.AddSeconds(61));

        await WaitAsync(jobId, "go", 3600, ct);
        await WaitAsync(jobId, "go", null, ct);

        Assert.Equal(due, (await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct)).DueAtUtc);
    }

    [Fact(DisplayName = "Once the stored expiry passes the slot turns Expired and every later wait answers TimedOut")]
    public async Task Passed_expiry_times_out_for_good()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        await WaitAsync(jobId, "go", 60, ct);
        var past = DateTime.UtcNow.AddMinutes(-1);
        await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal && c.CheckpointName == "go")
            .UpdateOnlyAsync(() => new CheckpointEntity { DueAtUtc = past }, ct);

        Assert.Equal(WaitSignalOutcomeCode.TimedOut, (await WaitAsync(jobId, "go", 60, ct)).Outcome);
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct)).Status);
        Assert.Equal(WaitSignalOutcomeCode.TimedOut, (await WaitAsync(jobId, "go", null, ct)).Outcome);
    }

    [Fact(DisplayName = "A wait bounded by an instant stores that instant, and a later instant neither moves nor clears it")]
    public async Task Instant_bound_is_armed_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var due = (await DbNowAsync(ct)).AddMinutes(10);

        Assert.Equal(WaitSignalOutcomeCode.SuspendPending, (await WaitUntilAsync(jobId, "go", due, ct)).Outcome);
        Assert.Equal(due, (await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct)).DueAtUtc);

        Assert.Equal(WaitSignalOutcomeCode.SuspendPending, (await WaitUntilAsync(jobId, "go", due.AddHours(1), ct)).Outcome);
        Assert.Equal(due, (await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct)).DueAtUtc);
    }

    [Fact(DisplayName = "A slot first armed at or past its instant is armed Expired and answers TimedOut at once")]
    public async Task Passed_instant_times_out_without_suspending()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var past = (await DbNowAsync(ct)).AddMinutes(-1);

        Assert.Equal(WaitSignalOutcomeCode.TimedOut, (await WaitUntilAsync(jobId, "go", past, ct)).Outcome);
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "go", ct)).Status);

        // An unbounded Pending slot takes a passed instant the same way, so a member armed without a bound times out too.
        await WaitAsync(jobId, "later", null, ct);
        Assert.Equal(WaitSignalOutcomeCode.TimedOut, (await WaitUntilAsync(jobId, "later", past, ct)).Outcome);
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadSlotAsync(jobId, CheckpointKindCode.Signal, "later", ct)).Status);
    }

    [Fact(DisplayName = "A Set slot of another kind does not satisfy a wait of the same name")]
    public async Task Slots_are_keyed_by_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var input = new JobControlInput(null, EventReason.SignalReleased, null);
        await Slots.RaiseSignalAsync(new RaiseSignalSlot(jobId, CheckpointKindCode.ChildLatch, "go", 0, null, input), ct);

        Assert.Equal(WaitSignalOutcomeCode.SuspendPending, (await WaitAsync(jobId, "go", null, ct)).Outcome);
        Assert.Equal(CheckpointStatusCode.Set, (await ReadSlotAsync(jobId, CheckpointKindCode.ChildLatch, "go", ct)).Status);
    }
}
