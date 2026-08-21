using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for durable child-wait timeouts: <c>TryWaitChildAsync</c> rides the same expiration the
/// signal waits use, an expired child latch resolves TimedOut once, and the resolution cancels the
/// awaited child and its descendants without ever touching the parent. A child landing terminal after
/// the flip revives nothing.
/// </summary>
[ConformanceSpec(
    "child-jobs.wait-timeout",
    "A bounded child wait expires, cancels its subtree, and leaves the parent running",
    Area = "ChildJobs",
    Contract = "A bounded child wait resolves TimedOut once, cancels the awaited child and its descendants, and leaves the parent free to continue.",
    Arrange = "Parents waiting on children with a 30-minute bound are registered so only a deliberate rewind of the stored expiration can expire a wait.",
    Act = "The runtime ticks parents and children around the persisted expiration, with children landing before it, after it, and never.",
    Assert = "A timed-out wait cancels the awaited subtree with job.wait-timed-out, spares siblings and the parent, and no late landing revives it."
)]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.WaitSignalAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.CancelJobAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.GetChildJobIdsAsync))]
public abstract class ChildTimeoutSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A child that lands before the deadline resolves Completed with its succeeded outcome")]
    public async Task Child_before_the_deadline_resolves_completed()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartParentAsync("job-child-quick", ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        // The child latch carries the deadline exactly as a signal slot does, and the Suspended parent
        // caches it so the claim can wake at that instant.
        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        Assert.Equal(JobCheckpointKindCode.ChildLatch, latch.Kind);
        Assert.NotNull(latch.DueAtUtc);
        Assert.Equal(latch.DueAtUtc, (await ReadJobAsync(parent.JobId, ct)).NextRunAtUtc);

        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(child, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        var report = await Jobs.GetResultAsync<ChildWaitReport>(parent, ct);
        Assert.NotNull(report);
        Assert.False(report!.TimedOut);
        Assert.True(report.Completed);
        Assert.Equal(child, report.ChildJobId);
        Assert.Equal(JobStatusCode.Succeeded, report.Outcome!.Status);
        Assert.True(report.Outcome.Succeeded);

        Assert.Equal(JobCheckpointStatusCode.Set, (await ReadSignalsAsync(parent.JobId, ct)).Single().Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(0, await CountReasonAsync(parent.JobId, JobEventReasonCode.JobWaitTimedOut, ct));
        Assert.Equal(0, await CountReasonAsync(child, JobEventReasonCode.JobWaitTimedOut, ct));
    }

    [Fact(DisplayName = "A child that fails or is cancelled before the deadline rides its status back to the parent")]
    public async Task Child_status_before_the_deadline_rides_back_to_the_parent()
    {
        var ct = TestContext.Current.CancellationToken;

        var failing = await StartParentAsync("job-child-fail", ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(failing, ct));
        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(await OnlyChildAsync(failing.JobId, ct), ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(failing, ct));
        var failed = await Jobs.GetResultAsync<ChildWaitReport>(failing, ct);
        Assert.True(failed!.Completed);
        Assert.Equal(JobStatusCode.Failed, failed.Outcome!.Status);

        var cancelled = await StartParentAsync("job-wait-signal", ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(cancelled, ct));
        var child = await OnlyChildAsync(cancelled.JobId, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(JobLookup.ById(child), "superseded", ct: ct)).Action);

        // The cancel raised the latch, so the parent is Ready without its deadline ever mattering.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(cancelled, ct));
        var stopped = await Jobs.GetResultAsync<ChildWaitReport>(cancelled, ct);
        Assert.True(stopped!.Completed);
        Assert.Equal(JobStatusCode.Cancelled, stopped.Outcome!.Status);
    }

    [Fact(DisplayName = "An expired child wait cancels the awaited subtree, spares the sibling, and leaves the parent running")]
    public async Task Expired_child_wait_cancels_the_subtree_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-parent-try-wait-child-subtree", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var children = await ChildIdsAsync(parent.JobId, ct);
        var slow = children["slow"];
        var sibling = children["sibling"];
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(slow, ct));
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(sibling, ct));
        var grandchild = await OnlyChildAsync(slow, ct);

        await ExpireChildWaitAsync(Db, parent.JobId, slow, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        // The parent is the one thing a child timeout must never touch: it resumed, ran the code after
        // the wait, and landed terminal on its own terms with an untouched failure budget.
        var parentJob = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, parentJob.Status);
        Assert.Equal(0, parentJob.FailureCount);
        Assert.Equal(1, await CountVariableAsync(parent.JobId, "ran.after", ct));
        Assert.Equal(1, await CountEventsAsync(parent.JobId, EventCode.JobNoteRecorded, ct));
        Assert.Equal(0, await CountEventsAsync(parent.JobId, EventCode.JobCancelled, ct));

        var report = await Jobs.GetResultAsync<ChildWaitReport>(parent, ct);
        Assert.True(report!.TimedOut);
        Assert.False(report.Completed);
        Assert.Equal(slow, report.ChildJobId);
        Assert.Null(report.Outcome);

        foreach (var cancelledId in (long[])[slow, grandchild])
        {
            var job = await ReadJobAsync(cancelledId, ct);
            Assert.Equal(JobStatusCode.Cancelled, job.Status);
            Assert.Equal(1, await CountEventsAsync(cancelledId, EventCode.JobCancelled, ct));
            var cancel = await ReadLatestEventAsync(cancelledId, EventCode.JobCancelled, ct);
            Assert.Equal(JobEventReasonCode.JobWaitTimedOut, cancel.ReasonCode);
        }

        // The cascade walks the awaited child's subtree, never the parent's other branches.
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(sibling, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(sibling, EventCode.JobCancelled, ct));
        Assert.Equal(
            JobCheckpointStatusCode.Expired,
            (await ReadSignalsAsync(parent.JobId, ct)).Single(s => s.Name == $"sys.child.{slow}").Status
        );
    }

    [Fact(DisplayName = "A parent that timed out on one child stays active and joins a replacement child")]
    public async Task Parent_starts_a_replacement_child_after_a_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, first) = await DriveToChildTimeoutAsync(ct);

        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(first, ct)).Status);
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(parent.JobId, ct)).Status);

        var second = (await ChildIdsAsync(parent.JobId, ct))["second"];
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(second, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        var report = await Jobs.GetResultAsync<ChildWaitReport>(parent, ct);
        Assert.True(report!.TimedOut);
        Assert.Equal(first, report.ChildJobId);
        Assert.Equal(second, report.Outcome!.ChildJobId);
        Assert.Equal(JobStatusCode.Succeeded, report.Outcome.Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(0, (await ReadJobAsync(parent.JobId, ct)).FailureCount);
    }

    [Fact(DisplayName = "A replay over an expired child latch re-runs the cancel as a no-op and still resolves TimedOut")]
    public async Task Replay_re_runs_the_cancel_as_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, first) = await DriveToChildTimeoutAsync(ct);
        Assert.Equal(1, await CountEventsAsync(first, EventCode.JobCancelled, ct));

        // Crash-replay the parent from the top: the slot is Expired, so the wait re-derives TimedOut and
        // the cancel runs again against a terminal child, which must add nothing to the ledger.
        await SetJobStatusReadyAsync(Db, parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        Assert.Equal(1, await CountEventsAsync(first, EventCode.JobCancelled, ct));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(first, ct)).Status);
        Assert.Equal(2, (await ChildIdsAsync(parent.JobId, ct)).Count);

        var second = (await ChildIdsAsync(parent.JobId, ct))["second"];
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(second, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.True((await Jobs.GetResultAsync<ChildWaitReport>(parent, ct))!.TimedOut);
        Assert.Equal(1, await CountEventsAsync(first, EventCode.JobCancelled, ct));
    }

    [Fact(DisplayName = "A child landing terminal on an expired latch writes no slot, releases no parent, and says so")]
    public async Task Late_child_completion_cannot_revive_an_expired_latch()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartParentAsync("job-child-quick", ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);

        // Stage the interleaving the slot lock arbitrates: the parent's wait already flipped the latch
        // Expired, and the child's completion transaction only starts afterwards.
        await ForceLatchExpiredAsync(Db, parent.JobId, child, ct);
        var before = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(child, ct));

        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        Assert.Equal(JobCheckpointStatusCode.Expired, latch.Status);
        Assert.Equal(0, latch.ValueFormatId);

        var after = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, after.Status);
        Assert.Equal(before.NextRunAtUtc, after.NextRunAtUtc);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(0, await CountEventsAsync(parent.JobId, EventCode.JobResumed, ct));

        // The completion changed nothing, but it happened: the timeline says so and says why, or an
        // operator sees a succeeded child beside a parent its outcome never reached.
        var raised = await ReadSingleEventAsync(parent.JobId, EventCode.JobSignalRaised, ct);
        Assert.Contains("already expired", raised.ReasonMessage ?? "", StringComparison.Ordinal);

        // The parent still resolves TimedOut, and its cancel of an already-succeeded child is rejected.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.True((await Jobs.GetResultAsync<ChildWaitReport>(parent, ct))!.TimedOut);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(child, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(child, EventCode.JobCancelled, ct));
    }

    [Fact(DisplayName = "A timed-out child lands Cancelled budget-neutrally with its retention stamped")]
    public async Task Timed_out_child_is_retention_eligible()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await StartParentAsync("job-wait-signal", ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = await OnlyChildAsync(parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(child, ct));

        await ExpireChildWaitAsync(Db, parent.JobId, child, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        // The terminal landing is what makes the abandoned child reachable by retention at all; a child
        // left Suspended forever is exactly the leak this slice exists to close.
        var job = await ReadJobAsync(child, ct);
        Assert.Equal(JobStatusCode.Cancelled, job.Status);
        Assert.NotNull(job.RetentionUntilUtc);
        Assert.Equal(0, job.FailureCount);
        Assert.Null(job.LeasedByWorkerId);

        var cancel = await ReadLatestEventAsync(child, EventCode.JobCancelled, ct);
        Assert.Equal(JobEventReasonCode.JobWaitTimedOut, cancel.ReasonCode);
        Assert.Equal("Parent's child wait timed out.", cancel.ReasonMessage);
    }

    [Fact(DisplayName = "An unbounded child wait still suspends with no due instant and stays unclaimable")]
    public async Task Unbounded_child_wait_keeps_its_old_shape()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        Assert.Equal(JobCheckpointKindCode.ChildLatch, latch.Kind);
        Assert.Equal(JobCheckpointStatusCode.Pending, latch.Status);
        Assert.Null(latch.DueAtUtc);

        var job = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, job.Status);
        Assert.Null(job.NextRunAtUtc);

        // The claim admits due Suspended rows, but a NULL next run stays Ready-only: an unbounded child
        // wait must park until its child's latch releases it.
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(parent, ct));
    }

    // ---------- helpers ----------

    private async Task<JobEnqueueOutcome> StartParentAsync(string childJobName, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-parent-try-wait-child", JobPayload.Json(new TryWaitChildStart(childJobName))),
            ct
        );

    // Drives the retry probe to the instant after its bounded wait expired: the first child is
    // cancelled, the replacement is started, and the parent is parked on the replacement's latch.
    private async Task<(JobEnqueueOutcome Parent, long First)> DriveToChildTimeoutAsync(CancellationToken ct)
    {
        var parent = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-parent-try-wait-child-then-retry", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var first = (await ChildIdsAsync(parent.JobId, ct))["first"];
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(first, ct));

        await ExpireChildWaitAsync(Db, parent.JobId, first, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        return (parent, first);
    }

    private async Task<long> OnlyChildAsync(long parentId, CancellationToken ct) =>
        Assert.Single(await Db.From<Job>().Where(j => j.ParentId == parentId).ToListAsync(ct)).Id;

    private async Task<IReadOnlyDictionary<string, long>> ChildIdsAsync(long parentId, CancellationToken ct)
    {
        var children = await Db.From<Job>().Where(j => j.ParentId == parentId).ToListAsync(ct);
        return children.ToDictionary(j => j.DeduplicationKey!, j => j.Id, StringComparer.Ordinal);
    }

    private async Task<int> CountReasonAsync(long jobId, JobEventReasonCode reason, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.ReasonCode == reason).CountAsync(ct);

    // Moves the child wait past its deadline the way real time would: the latch's stored expiration and
    // the parent's cached claim instant both go into the past, and nothing else is touched.
    private static async Task ExpireChildWaitAsync(IDbSession db, long parentId, long childId, CancellationToken ct)
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        await db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET due_at_utc = @p_due WHERE job_id = @p_id AND kind_code = 50 AND name = @p_name",
            ct,
            ("@p_due", past),
            ("@p_id", parentId),
            ("@p_name", $"sys.child.{childId}")
        );
        await db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", past),
            ("@p_id", parentId)
        );
    }

    // Puts the latch straight into the state a resolved timeout leaves behind, without running the
    // parent: the only way to let a child complete strictly after the flip and strictly before the
    // parent acts on it.
    private static async Task ForceLatchExpiredAsync(IDbSession db, long parentId, long childId, CancellationToken ct)
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        await db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET status_code = @p_status, due_at_utc = @p_due "
                + "WHERE job_id = @p_id AND kind_code = 50 AND name = @p_name",
            ct,
            ("@p_status", (byte)JobCheckpointStatusCode.Expired),
            ("@p_due", past),
            ("@p_id", parentId),
            ("@p_name", $"sys.child.{childId}")
        );
        await db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", past),
            ("@p_id", parentId)
        );
    }

    private static Task SetJobStatusReadyAsync(IDbSession db, long jobId, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Ready),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );
}
