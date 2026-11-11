using System.Text.Json;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// A parent that has already finished still records what its child does afterwards. The child's latch on the parent is
/// written whatever the parent's status, so a restart of the parent replays its wait against the child's newest
/// outcome, not against the one it happened to see before it finished. A finished parent is never woken by it.
/// </summary>
[ConformanceSpec(
    "child-jobs.outcome-on-finished-parent",
    "A finished parent still records what its child did afterwards",
    Area = "ChildJobs",
    Contract = "A child that lands again or is cancelled after its parent finished records its newer outcome on the parent, which a restart of the parent reads."
)]
public sealed class ChildOutcomeOnFinishedParentSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobsManifest>
{
    [Fact(DisplayName = "A child that runs again while its parent is finished replaces its outcome on the parent")]
    public async Task A_child_rerun_under_a_finished_parent_replaces_its_outcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await FinishedParentOfAFailedChildAsync(ct);

        // An operator restarts the failed child, which lands again under a parent that has already finished.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(new JobRef(child.JobRef), ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(child.Id, ct));

        Assert.Equal((JobStatusCode.Failed, 2), await LatchAsync(parent.JobId, child.Id, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A restarted parent reads the outcome its child took after the parent had finished")]
    public async Task A_restarted_parent_reads_the_outcome_its_child_took_since()
    {
        var ct = TestContext.Current.CancellationToken;
        var (parent, child) = await FinishedParentOfAFailedChildAsync(ct);
        Assert.Equal(JobStatusCode.Failed, (await Jobs.GetResultAsync<ChildJobOutcome>(parent, ct))!.Status);

        // The child is restarted and then cancelled: a different outcome from the one the parent finished on.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(new JobRef(child.JobRef), ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(new JobRef(child.JobRef), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);

        // The restarted parent replays its wait and stores what it reads as its own result.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(parent, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal(JobStatusCode.Cancelled, (await Jobs.GetResultAsync<ChildJobOutcome>(parent, ct))!.Status);
    }

    [Fact(DisplayName = "A child cancelled while its parent is finished records the cancel on a parent that never waited for it")]
    public async Task A_child_cancelled_under_a_finished_parent_records_the_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-fire-and-forget", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).ToListAsync(ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(new JobRef(child.JobRef), ct: ct)).Action);

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(parent.JobId, ct)).Status);
        // A cancel names no execution: the child never ran.
        Assert.Equal((JobStatusCode.Cancelled, 0), await LatchAsync(parent.JobId, child.Id, ct));
    }

    [Fact(DisplayName = "A child cancelled by an ancestor's cancel records the cancel on its finished parent")]
    public async Task A_child_cancelled_by_an_ancestors_walk_records_the_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(root, ct));
        var finished = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
                TestNamespace,
                "job-child-echo",
                JobPayload.Json(new ChildEcho(1)),
                DeduplicationKey: "done",
                ParentJobId: root.JobId
            ),
            ct
        );
        var live = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None, DeduplicationKey: "held", ParentJobId: finished.JobId),
            ct
        );
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(finished, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(root, "stop the job tree", ct: ct)).Action);

        // The walk passes through the finished job to cancel the live one below it, and leaves the cancel on the finished job.
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(finished.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(live.JobId, ct)).Status);
        Assert.Equal((JobStatusCode.Cancelled, 0), await LatchAsync(finished.JobId, live.JobId, ct));
    }

    /// <summary>A parent that waited for its child, read the child's failure, and finished.</summary>
    private async Task<(EnqueueJobResponse Parent, JobEntity Child)> FinishedParentOfAFailedChildAsync(CancellationToken ct)
    {
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-of-failing-child", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));
        var child = Assert.Single(await Db.From<JobEntity>().Where(j => j.ParentJobId == parent.JobId).ToListAsync(ct));
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(child.Id, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(parent, ct));
        Assert.Equal((JobStatusCode.Failed, 1), await LatchAsync(parent.JobId, child.Id, ct));
        return (parent, child);
    }

    /// <summary>The child's latch on its parent as (status, execution), read from the stored envelope.</summary>
    private async Task<(JobStatusCode Status, int ExecutionNumber)> LatchAsync(long parentId, long childId, CancellationToken ct)
    {
        var latch = Assert.Single(await ReadSignalsAsync(parentId, ct), s => s.CheckpointName == $"sys.child.{childId}");
        Assert.Equal(CheckpointStatusCode.Set, latch.Status);
        using var envelope = JsonDocument.Parse(latch.Value);
        var root = envelope.RootElement;
        Assert.Equal(childId, root.GetProperty("childJobId").GetInt64());
        return (
            (JobStatusCode)root.GetProperty("status").GetInt32(),
            root.TryGetProperty("executionNumber", out var execution) ? execution.GetInt32() : 0
        );
    }
}
