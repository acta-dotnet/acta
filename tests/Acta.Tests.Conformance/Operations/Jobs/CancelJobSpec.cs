using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for cancel's status guards: a live job cancels with a retention stamp, a cancelled one is already in
/// state and walks its tree again, and a descendant walk cancels only while the job it walks is still Cancelled.
/// </summary>
[ConformanceSpec(
    "cancel-job.state-guard",
    "Cancel stamps retention once and walks only a cancelled job's tree",
    Area = "Control",
    Contract = "A cancel stamps retention once, a re-cancel is AlreadyInState yet finishes the walk, and a walk outliving a restart spares the new children."
)]
[Deploys("add-numbers", "job-wait-signal")]
public sealed class CancelJobSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<CancelJobSpec>>
{
    // The database clock, which is the one the verbs stamp their instants from.
    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static JobControlInput ControlInput(string message) => new("test", null, message);

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        JobStateStaging.SetStatusAsync(db, jobId, statusCode, ct);

    private static Task SetFailureCountAsync(IDbSession db, long jobId, short count, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET failure_count = @p_count WHERE job_id = @p_id",
            ct,
            ("@p_count", count),
            ("@p_id", jobId)
        );

    [Fact(DisplayName = "Cancel from Suspended and Executing is Applied and stamps retention_until_utc")]
    public async Task Cancel_from_Suspended_and_Executing_is_Applied_and_stamps_retention()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("cancel non-terminal");

        var suspendedJob = await EnqueueAsync(ct);
        var executingJob = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, suspendedJob, (byte)JobStatusCode.Suspended, ct);
        await SetJobStatusAsync(Db, executingJob, (byte)JobStatusCode.Executing, ct);

        // Default retention is 90 days so retention_until_utc will be ~now + 90d; capture a window.
        // Both ends come off the DATABASE clock, because the value being bracketed does: comparing a
        // db_now-derived instant against this process's clock would fold container clock skew into a
        // one-second lower bound, and skew is not what this fact is about.
        var tBefore = (await NowAsync(ct)).AddSeconds(-1);
        var cancelSuspended = await Services
            .GetRequiredService<CancelJobHandler>()
            .CancelAsync(suspendedJob, input, fence: null, walkRootJobId: null, ct);
        var cancelExecuting = await Services
            .GetRequiredService<CancelJobHandler>()
            .CancelAsync(executingJob, input, fence: null, walkRootJobId: null, ct);
        var tAfter = (await NowAsync(ct)).AddDays(91);

        Assert.Equal(ControlAction.Applied, cancelSuspended.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancelSuspended.Status);
        Assert.Equal(ControlAction.Applied, cancelExecuting.Action);
        Assert.Equal(JobStatusCode.Cancelled, cancelExecuting.Status);

        var suspendedState = await ReadJobAsync(suspendedJob, ct);
        Assert.Equal(JobStatusCode.Cancelled, suspendedState.Status);
        Assert.NotNull(suspendedState.RetentionUntilUtc);
        Assert.InRange(suspendedState.RetentionUntilUtc!.Value, tBefore, tAfter);

        var executingState = await ReadJobAsync(executingJob, ct);
        Assert.Equal(JobStatusCode.Cancelled, executingState.Status);
        Assert.NotNull(executingState.RetentionUntilUtc);
        Assert.InRange(executingState.RetentionUntilUtc!.Value, tBefore, tAfter);
    }

    [Fact(DisplayName = "Re-cancel of a Cancelled job is AlreadyInState and writes nothing, its retention_until_utc included")]
    public async Task Recancel_is_AlreadyInState_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = ControlInput("cancel then re-cancel");

        var jobId = await EnqueueAsync(ct);

        var first = await Services.GetRequiredService<CancelJobHandler>().CancelAsync(jobId, input, fence: null, walkRootJobId: null, ct);
        Assert.Equal(ControlAction.Applied, first.Action);
        Assert.Equal(JobStatusCode.Cancelled, first.Status);

        var afterFirst = await ReadJobAsync(jobId, ct);

        var second = await Jobs.CancelAsync(JobLookup.ById(jobId), "cancel again", ct: ct);
        Assert.Equal(ControlAction.AlreadyInState, second.Action);
        Assert.Equal(JobStatusCode.Cancelled, second.Status);
        Assert.Equal(afterFirst.Version, second.Version);

        var afterSecond = await ReadJobAsync(jobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, afterSecond.Status);
        Assert.Equal(afterFirst.Version, afterSecond.Version);
        Assert.Equal(afterFirst.RetentionUntilUtc, afterSecond.RetentionUntilUtc);
        Assert.Equal(1, await CountEventsAsync(jobId, EventCode.JobCancelled, ct));
    }

    [Fact(DisplayName = "A re-cancel of a Cancelled parent is AlreadyInState and cancels the child a cut-short walk left running")]
    public async Task Recancel_finishes_a_walk_cut_short()
    {
        var ct = TestContext.Current.CancellationToken;
        var parentId = await EnqueueAsync(ct);
        var child = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
                TestNamespace,
                "add-numbers",
                JobPayload.Json(new AddNumbers(2, 3)),
                IdempotencyKey: Unique("child"),
                ParentJobId: parentId
            ),
            ct
        );

        // The cancel by id runs no walk, so the child is left running as a process that died after the commit leaves it.
        var first = await Services
            .GetRequiredService<CancelJobHandler>()
            .CancelAsync(parentId, ControlInput("stop the tree"), fence: null, walkRootJobId: null, ct);
        Assert.Equal(ControlAction.Applied, first.Action);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(child.JobId, ct)).Status);

        var again = await Jobs.CancelAsync(JobLookup.ById(parentId), "finish the walk", ct: ct);

        Assert.Equal(ControlAction.AlreadyInState, again.Action);
        Assert.Equal(1, await CountEventsAsync(parentId, EventCode.JobCancelled, ct));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(child.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A descendant walk that outlives a restart of its root leaves the restarted job's new children alone")]
    public async Task A_walk_outliving_a_restart_spares_the_new_children()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(root, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(root, "stop the job tree", ct: ct)).Action);

        // The root restarts and its new execution starts a child before the first cancel's walk comes back to it.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(root, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(root, ct));
        var child = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(
                TestNamespace,
                "job-wait-signal",
                JobPayload.None,
                IdempotencyKey: "after-restart",
                ParentJobId: root.JobId
            ),
            ct
        );

        await Services.GetRequiredService<JobsOperations>().CancelDescendantsAsync(root.JobId, ct);

        Assert.NotEqual(JobStatusCode.Cancelled, (await ReadJobAsync(child.JobId, ct)).Status);
        Assert.Equal(0, await CountEventsAsync(child.JobId, EventCode.JobCancelled, ct));
    }
}
