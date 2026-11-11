using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Signals;

/// <summary>Conformance for the engine's wait decision on one checkpoint: arm and suspend, continue on Set, or time out once the stored expiry passes.</summary>
[ConformanceSpec(
    "wait-job-signal.decision",
    "A wait arms its checkpoint once and answers suspend, continue, or timed out",
    Area = "Signals",
    Contract = "Wait arms a Pending checkpoint once, continues with a Set one's payload, and answers TimedOut past a stored expiry or at once when first armed past an instant."
)]
public sealed class WaitJobSignalSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    private SignalsOperations Signals => Services.GetRequiredService<SignalsOperations>();

    // The wait of the execution EnqueueAsync stages, bounded by a length or by an instant.
    private Task<WaitJobSignalResponse> WaitAsync(long jobId, string name, int? timeoutSeconds, CancellationToken ct) =>
        Signals.WaitJobSignalAsync(Staged(jobId), CheckpointKindCode.Signal, name, timeoutSeconds, dueAtUtc: null, ct);

    private Task<WaitJobSignalResponse> WaitUntilAsync(long jobId, string name, DateTime dueAtUtc, CancellationToken ct) =>
        Signals.WaitJobSignalAsync(Staged(jobId), CheckpointKindCode.Signal, name, timeoutSeconds: null, dueAtUtc, ct);

    private static ExecutionFence Staged(long jobId) => new(jobId, 1, JobStateStaging.StagedWorkerId);

    // DB now cut to whole seconds, so the instant a test arms survives every provider's stored precision unchanged.
    private async Task<DateTime> DbNowAsync(CancellationToken ct)
    {
        var now = await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private async Task<CheckpointEntity> ReadCheckpointAsync(long jobId, CheckpointKindCode kind, string name, CancellationToken ct) =>
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

    [Fact(DisplayName = "A first wait arms a Pending checkpoint and answers SuspendPending without touching the job")]
    public async Task First_wait_arms_a_pending_checkpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var before = await ReadJobAsync(jobId, ct);

        var decision = await WaitAsync(jobId, "go", null, ct);

        Assert.Equal(WaitJobSignalOutcomeCode.SuspendPending, decision.Outcome);
        Assert.Null(decision.Value);
        var checkpoint = await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct);
        Assert.Equal(CheckpointStatusCode.Pending, checkpoint.Status);
        Assert.Null(checkpoint.DueAtUtc);

        // The decision is the caller's to act on: the wait itself never moves the job.
        var after = await ReadJobAsync(jobId, ct);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact(DisplayName = "A repeat wait while Pending answers SuspendPending and leaves the checkpoint untouched")]
    public async Task Repeat_wait_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        await WaitAsync(jobId, "go", null, ct);
        var armed = await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct);

        Assert.Equal(WaitJobSignalOutcomeCode.SuspendPending, (await WaitAsync(jobId, "go", null, ct)).Outcome);

        var checkpoint = await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct);
        Assert.Equal(armed.Version, checkpoint.Version);
        Assert.Equal(CheckpointStatusCode.Pending, checkpoint.Status);
    }

    [Fact(DisplayName = "A wait on a Set checkpoint continues with the stored payload")]
    public async Task Set_checkpoint_continues_with_its_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var payload = JobPayload.Json(new ReviewDecision(true, "approved"));
        await Jobs.RaiseSignalAsync(JobLookup.ById(jobId), "review", payload, ct: ct);

        var decision = await WaitAsync(jobId, "review", 60, ct);

        Assert.Equal(WaitJobSignalOutcomeCode.ContinueSet, decision.Outcome);
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
        var due = Assert.IsType<DateTime>((await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct)).DueAtUtc);
        Assert.InRange(due, start.AddSeconds(59), end.AddSeconds(61));

        await WaitAsync(jobId, "go", 3600, ct);
        await WaitAsync(jobId, "go", null, ct);

        Assert.Equal(due, (await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct)).DueAtUtc);
    }

    [Fact(DisplayName = "Once the stored expiry passes the checkpoint turns Expired and every later wait answers TimedOut")]
    public async Task Passed_expiry_times_out_for_good()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        await WaitAsync(jobId, "go", 60, ct);
        var past = DateTime.UtcNow.AddMinutes(-1);
        await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Signal && c.CheckpointName == "go")
            .UpdateOnlyAsync(() => new CheckpointEntity { DueAtUtc = past }, ct);

        Assert.Equal(WaitJobSignalOutcomeCode.TimedOut, (await WaitAsync(jobId, "go", 60, ct)).Outcome);
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct)).Status);
        Assert.Equal(WaitJobSignalOutcomeCode.TimedOut, (await WaitAsync(jobId, "go", null, ct)).Outcome);
    }

    [Fact(DisplayName = "A wait bounded by an instant stores that instant, and a later instant neither moves nor clears it")]
    public async Task Instant_bound_is_armed_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var due = (await DbNowAsync(ct)).AddMinutes(10);

        Assert.Equal(WaitJobSignalOutcomeCode.SuspendPending, (await WaitUntilAsync(jobId, "go", due, ct)).Outcome);
        Assert.Equal(due, (await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct)).DueAtUtc);

        Assert.Equal(WaitJobSignalOutcomeCode.SuspendPending, (await WaitUntilAsync(jobId, "go", due.AddHours(1), ct)).Outcome);
        Assert.Equal(due, (await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct)).DueAtUtc);
    }

    [Fact(DisplayName = "A checkpoint first armed at or past its instant is armed Expired and answers TimedOut at once")]
    public async Task Passed_instant_times_out_without_suspending()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var past = (await DbNowAsync(ct)).AddMinutes(-1);

        Assert.Equal(WaitJobSignalOutcomeCode.TimedOut, (await WaitUntilAsync(jobId, "go", past, ct)).Outcome);
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct)).Status);

        // An unbounded Pending checkpoint takes a passed instant the same way, so a member armed without a bound times out too.
        await WaitAsync(jobId, "later", null, ct);
        Assert.Equal(WaitJobSignalOutcomeCode.TimedOut, (await WaitUntilAsync(jobId, "later", past, ct)).Outcome);
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "later", ct)).Status);
    }

    [Fact(DisplayName = "A raise that waited for the job while a wait expired leaves the Expired checkpoint as it is")]
    public async Task A_raise_queued_behind_an_expiring_wait_does_not_revive_it()
    {
        // SQL Server's raise takes the checkpoint's own lock before it reads, and SQLite runs one writer at a time; on
        // PostgreSQL the raise reads an absent checkpoint, then waits for the job row the expiring wait holds.
        Assert.SkipUnless(Db.Provider == DbProvider.Postgres, "Only PostgreSQL reads the checkpoint before the job lock.");
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);

        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var waiter = await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            // The wait, armed at an instant already past, inserts its checkpoint Expired and holds the job row to commit.
            cmd.Transaction = waiter;
            cmd.CommandText =
                $"SELECT outcome_code FROM {Db.Schema}.wait_job_signal({jobId}::bigint, 1, {JobStateStaging.StagedWorkerId}, "
                + "20 /* CheckpointKindCode.Signal */::smallint, 'go'::varchar, NULL::int, now() - interval '1 minute')";
            Assert.Equal((short)WaitJobSignalOutcomeCode.TimedOut, Convert.ToInt16(await cmd.ExecuteScalarAsync(ct)));
        }

        var raise = Services
            .GetRequiredService<SignalsOperations>()
            .RaiseJobSignalAsync(
                new RaiseJobSignalCheckpoint(
                    jobId,
                    CheckpointKindCode.Signal,
                    "go",
                    JobPayloadFormat.None.Id,
                    null,
                    new JobControlInput(null, EventReason.SignalReleased, null)
                ),
                ct
            );
        await LockWaitProbe.WaitAsync(Db, "raise_job_signal", LockWait.Row, raise, waiter, ct);
        await waiter.CommitAsync(ct);
        await raise;

        Assert.Equal(CheckpointStatusCode.Expired, (await ReadCheckpointAsync(jobId, CheckpointKindCode.Signal, "go", ct)).Status);
        Assert.Equal(WaitJobSignalOutcomeCode.TimedOut, (await WaitAsync(jobId, "go", null, ct)).Outcome);
    }

    [Fact(DisplayName = "A Set checkpoint of another kind does not satisfy a wait of the same name")]
    public async Task Checkpoints_are_keyed_by_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobId = await EnqueueAsync(ct);
        var input = new JobControlInput(null, EventReason.SignalReleased, null);
        await Signals.RaiseJobSignalAsync(new RaiseJobSignalCheckpoint(jobId, CheckpointKindCode.ChildLatch, "go", 0, null, input), ct);

        Assert.Equal(WaitJobSignalOutcomeCode.SuspendPending, (await WaitAsync(jobId, "go", null, ct)).Outcome);
        Assert.Equal(CheckpointStatusCode.Set, (await ReadCheckpointAsync(jobId, CheckpointKindCode.ChildLatch, "go", ct)).Status);
    }
}
