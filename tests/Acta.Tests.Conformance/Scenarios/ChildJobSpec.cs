using System.Data.Common;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.ChildLatches;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for child jobs: <c>ctx.StartChildAsync</c> enqueues a name-deduped child through the
/// shared enqueue path, a terminal child raises a durable <c>sys.child.{id}</c> latch on its parent
/// (releasing a Suspended parent), waits return <see cref="ChildJobOutcome"/> records, and cancel
/// cascades to the non-terminal descendant subtree.
/// </summary>
[ConformanceSpec(
    "child-jobs.start-wait-cascade",
    "Child jobs start deduped, join on completion latches, and cancel cascades",
    Area = "ChildJobs",
    Contract = "StartChildAsync dedupes by name per parent and a terminal child raises a durable latch that releases a waiting parent while cancel cascades to the live subtree.",
    Arrange = "A parent job and named child definitions are registered, with the parent set to wait on its children.",
    Act = "The parent starts named children that finish in any order, fail, exhaust their budget, or are cancelled with an ancestor.",
    Assert = "Terminal children raise durable sys.child latches that release or fail the waiting parent, and cancel cascades to the live subtree."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.CancelJobAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.CancelJobAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ReclaimStuckJobsAsync))]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.WaitSignalAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.GetChildJobIdsAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.GetStaleChildLatchesAsync))]
public abstract class ChildJobSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int LeaseTtlSeconds = -5;

    [Fact(DisplayName = "A terminal child sets a durable sys.child latch that releases the Suspended parent, which reads the child result")]
    public async Task Parent_starts_child_waits_on_latch_and_reads_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(parent.JobId, ct)).Status);

        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
        Assert.Equal("echo", child.DeduplicationKey);
        Assert.Equal(parent.JobId, child.LineageRootId);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(child.Id, ct));

        var released = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, released.Status);
        var latch = Assert.Single(await ReadSignalsAsync(parent.JobId, ct));
        Assert.Equal($"sys.child.{child.Id}", latch.Name);
        Assert.Equal(JobCheckpointStatusCode.Set, latch.Status);
        Assert.Equal(1, await CountEventsAsync(parent.JobId, EventCode.JobSignalRaised, ct));
        Assert.Equal(1, await CountEventsAsync(parent.JobId, EventCode.JobResumed, ct));

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(42, (await Jobs.GetResultAsync<ChildEchoResult>(parent, ct))!.Doubled);
    }

    [Fact(DisplayName = "Child start is replay-deduped by name per parent")]
    public async Task Child_start_is_replay_deduped_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        // Force a replay while the child is still pending: the re-run StartChildAsync must dedupe.
        await SetJobStatusReadyAsync(Db, parent.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
    }

    [Fact(DisplayName = "WaitChildAsync returns an outcome record and never throws on child failure")]
    public async Task Failed_child_returns_outcome_record_and_envelope_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-of-failing-child", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
        Assert.Equal(RunOnceOutcome.Failed, await Runtime.RunOnceAsync(child.Id, ct));

        // The parent's wait never throws: it returns the outcome record (terminal status only), which
        // the parent stores as its own result, round-tripping the SQL-built JSON envelope through the parser.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);

        var outcome = await Jobs.GetResultAsync<ChildJobOutcome>(parent, ct);
        Assert.NotNull(outcome);
        Assert.Equal(child.Id, outcome!.ChildJobId);
        Assert.Equal(JobStatusCode.Failed, outcome.Status);
        Assert.False(outcome.Succeeded);
    }

    [Fact(DisplayName = "WaitChildrenAsync joins all children regardless of completion order")]
    public async Task Wait_children_joins_all_regardless_of_completion_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-many", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var children = await ReadChildrenAsync(parent.JobId, ct);
        Assert.Equal(3, children.Count);

        // Complete out of order: c, a, b. Each terminal child releases the parent, which replays and
        // either suspends on the next pending latch or finishes.
        foreach (var key in (string[])["c", "a", "b"])
        {
            var child = children.Single(j => j.DeduplicationKey == key);
            Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(child.Id, ct));
            if ((await ReadJobAsync(parent.JobId, ct)).Status == JobStatusCode.Ready)
            {
                await Runtime.RunOnceAsync(parent, ct);
            }
        }

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(12, (await Jobs.GetResultAsync<ChildEchoResult>(parent, ct))!.Doubled);
    }

    [Fact(DisplayName = "Operator cancel cascades to the non-terminal descendant subtree with reason ParentCancelled")]
    public async Task Operator_cancel_cascades_to_the_live_subtree_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(root, ct));

        // Attach descendants through the public ParentId wire: a 3-level chain, plus a finished child
        // that itself has a live grandchild, so the walk must pass through the terminal node.
        var child = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None, DeduplicationKey: "held", ParentId: root.JobId),
            ct
        );
        var grandchild = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None, DeduplicationKey: "held", ParentId: child.JobId),
            ct
        );
        var done = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "job-child-echo",
                JobPayload.Json(new ChildEcho(1)),
                DeduplicationKey: "done",
                ParentId: root.JobId
            ),
            ct
        );
        var behindDone = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None, DeduplicationKey: "held", ParentId: done.JobId),
            ct
        );
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(done, ct));

        var cancel = await Jobs.CancelAsync(root, "stop the job tree", ct: ct);
        Assert.Equal(JobControlAction.Applied, cancel.Action);

        var rootJob = await ReadJobAsync(root.JobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, rootJob.Status);
        var rootCancelEvent = await ReadLatestEventAsync(root.JobId, EventCode.JobCancelled, ct);
        Assert.Equal(JobEventReasonCode.JobControlManual, rootCancelEvent.ReasonCode);

        foreach (var descendantId in (long[])[child.JobId, grandchild.JobId, behindDone.JobId])
        {
            var descendant = await ReadJobAsync(descendantId, ct);
            Assert.Equal(JobStatusCode.Cancelled, descendant.Status);
            var descendantCancelEvent = await ReadLatestEventAsync(descendantId, EventCode.JobCancelled, ct);
            Assert.Equal(JobEventReasonCode.JobParentCancelled, descendantCancelEvent.ReasonCode);
            Assert.Equal(1, await CountEventsAsync(descendantId, EventCode.JobCancelled, ct));
        }

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(done.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A handler self-cancel cascades to its children with reason ParentCancelled")]
    public async Task Handler_cancel_cascades_to_children()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-cancel-self", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));

        var parentJob = await ReadJobAsync(parent.JobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, parentJob.Status);
        var parentCancelEvent = await ReadLatestEventAsync(parent.JobId, EventCode.JobCancelled, ct);
        Assert.Equal(JobEventReasonCode.JobHandlerCancelled, parentCancelEvent.ReasonCode);

        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
        Assert.Equal(JobStatusCode.Cancelled, child.Status);
        var childCancelEvent = await ReadLatestEventAsync(child.Id, EventCode.JobCancelled, ct);
        Assert.Equal(JobEventReasonCode.JobParentCancelled, childCancelEvent.ReasonCode);
    }

    [Fact(DisplayName = "Parent completion never cascades and a raise to a terminal parent is a no-op")]
    public async Task Parent_completion_never_cascades_and_late_raise_noops()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-fire-and-forget", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);

        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));
        Assert.Equal(JobStatusCode.Ready, child.Status);

        // The orphaned child still runs; its raise hits a terminal parent and writes nothing.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(child.Id, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(child.Id, ct)).Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Empty(await ReadSignalsAsync(parent.JobId, ct));
    }

    [Fact(DisplayName = "A terminal parent rejects new children")]
    public async Task Terminal_parent_rejects_new_children()
    {
        var ct = TestContext.Current.CancellationToken;
        var done = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-child-echo", JobPayload.Json(new ChildEcho(1))), ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(done, ct));

        await Assert.ThrowsAnyAsync<DbException>(async () =>
            await Jobs.EnqueueAsync(
                new JobEnqueueRequest(TestNamespace, "job-child-echo", JobPayload.Json(new ChildEcho(2)), ParentId: done.JobId),
                ct
            )
        );
    }

    [Fact(DisplayName = "User signals cannot use the reserved sys.child latch namespace")]
    public async Task User_signals_cannot_use_the_child_latch_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.RaiseSignalAsync(job, "sys.child.1", ct: ct));
    }

    [Fact(DisplayName = "Reclaim exhausting a child's budget reports the pair whose latch raise releases the waiting parent")]
    public async Task Reclaim_exhaustion_of_a_child_releases_the_waiting_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));

        var dialect = Services.GetRequiredService<ISqlDialect>();
        var workerId = await WorkerIdAsync(ns, ct);
        short maxAttempts;
        {
            var def = await Db.From<JobDefinition>().Where(d => d.NamespaceId == ns && d.Name == "job-child-echo").SingleOrDefaultAsync(ct);
            maxAttempts = def!.MaxAttempts;
        }

        // Burn the child's whole budget through expired leases; the final reclaim lands it Failed and
        // reports the (child, parent) pair, whose raise (RecoveryJob's follow-up in production)
        // must release the parent, or the parent would hang forever.
        var failed = new List<(long ChildId, long ParentId)>();
        for (short attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, workerId, LeaseTtlSeconds, child.Id, ct));
            var reclaim = await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct);
            Assert.Equal(1, reclaim.Reclaimed);
            failed.AddRange(reclaim.FailedChildren);
        }

        var (childId, parentId) = Assert.Single(failed);
        Assert.Equal(child.Id, childId);
        Assert.Equal(parent.JobId, parentId);
        Assert.True(await RaiseChildLatch.Run(Services.GetRequiredService<ISignalStore>(), childId, parentId, JobStatusCode.Failed, ct));

        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(child.Id, ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Equal(JobCheckpointStatusCode.Set, Assert.Single(await ReadSignalsAsync(parent.JobId, ct)).Status);
    }

    [Fact(DisplayName = "The maintenance sweep re-raises a stale latch lost to a crash and releases the parent")]
    public async Task Maintenance_sweep_re_raises_a_stale_latch()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-parent-one", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await ReadChildrenAsync(parent.JobId, ct));

        // Simulate a raise lost to a crash: the child lands terminal without its latch being set.
        await SetJobStatusAsync(Db, child.Id, 220, ct);

        var latch = Assert.Single(
            (await Services.GetRequiredService<IExecutionStore>().GetStaleChildLatchesAsync(ns, ct)).Where(l =>
                l.ParentJobId == parent.JobId
            )
        );
        Assert.Equal(child.Id, latch.ChildJobId);
        Assert.Equal(JobStatusCode.Cancelled, latch.ChildStatus);

        Assert.True(
            await RaiseChildLatch.Run(
                Services.GetRequiredService<ISignalStore>(),
                latch.ChildJobId,
                latch.ParentJobId,
                latch.ChildStatus!.Value,
                ct
            )
        );
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(parent.JobId, ct)).Status);
        Assert.Empty(
            (await Services.GetRequiredService<IExecutionStore>().GetStaleChildLatchesAsync(ns, ct)).Where(l =>
                l.ParentJobId == parent.JobId
            )
        );
    }

    [Fact(DisplayName = "Concurrent cancel and child completions converge with the whole tree terminal and no error")]
    public async Task Concurrent_cancel_and_child_completions_converge()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(root, ct));

        var children = new JobEnqueueOutcome[5];
        for (var i = 0; i < children.Length; i++)
        {
            children[i] = await Jobs.EnqueueAsync(
                new JobEnqueueRequest(
                    TestNamespace,
                    "job-child-echo",
                    JobPayload.Json(new ChildEcho(i)),
                    DeduplicationKey: $"c{i}",
                    ParentId: root.JobId
                ),
                ct
            );
        }

        // Race the cascade against in-flight completions: every interleaving must converge with the
        // whole tree terminal and no surfaced error. Single-row cancel transactions plus the store's
        // bounded deadlock retry recover the claim-vs-cancel index lock race under load.
        var runs = children.Select(c => Task.Run(() => Runtime.RunOnceAsync(c.JobId, ct), ct)).ToArray();
        var cancel = Task.Run(async () => Assert.Equal(JobControlAction.Applied, (await Jobs.CancelAsync(root, ct: ct)).Action), ct);
        await Task.WhenAll([.. runs, cancel]);

        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(root.JobId, ct)).Status);
        foreach (var child in children)
        {
            var job = await ReadJobAsync(child.JobId, ct);
            Assert.True(job.Status is JobStatusCode.Succeeded or JobStatusCode.Cancelled, $"child {child.JobId} ended {job.Status}");
        }
    }

    // ---------- helpers ----------

    private async Task<IReadOnlyList<TestJobRow>> ReadChildrenAsync(long parentId, CancellationToken ct)
    {
        var children = await Db.From<Job>().Where(j => j.ParentId == parentId).ToListAsync(ct);
        var rows = new List<TestJobRow>(children.Count);
        foreach (var child in children)
        {
            rows.Add(await ReadJobAsync(child.Id, ct));
        }

        return rows;
    }

    private async Task<int> WorkerIdAsync(short ns, CancellationToken ct)
    {
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return worker!.Id;
    }

    private static Task SetJobStatusReadyAsync(IDbSession db, long jobId, CancellationToken ct) =>
        SetJobStatusAsync(db, jobId, (byte)JobStatusCode.Ready, ct);

    private static Task SetJobStatusAsync(IDbSession db, long jobId, int statusCode, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", statusCode),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );
}
