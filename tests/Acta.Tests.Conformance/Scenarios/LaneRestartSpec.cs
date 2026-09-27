using Acta.Relational.Commands;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Restarting a finished laned job reactivates the same row under the lane lock. It goes Ready only when
/// no other member of its lane runs and no older one is unfinished, and otherwise waits Blocked; being
/// older than every member waiting, it is promoted first once the lane moves on. Deadlock retry is off, so
/// a restart racing a completion fails as a deadlock rather than retrying past it.
/// </summary>
[ConformanceSpec(
    "lanes.restart",
    "Restarting a finished laned job reactivates it in place",
    Area = "Lanes",
    Contract = "A restart of a finished laned job reactivates the same row, Blocked while another member runs and Ready otherwise, and it runs next.",
    Arrange = "Laned jobs have finished in a private namespace, some with live members behind them and some with laned descendants.",
    Act = "An operator restarts finished members, alone, repeated with one version, and racing the running member's completion.",
    Assert = "Each lane keeps at most one running member, the restarted job runs before the members already waiting, and lineage cycles are refused."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RestartJobAsync))]
public abstract class LaneRestartSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Rounds = 20;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        var provider = services.Last(d => d.ServiceType == typeof(SqlProviderOptions));
        services.Remove(provider);
        services.AddSingleton(sp =>
        {
            var options = (SqlProviderOptions)provider.ImplementationFactory!(sp);
            options.DeadlockRetryAttempts = 1;
            return options;
        });
    }

    [Fact(DisplayName = "A restarted failed head waits Blocked behind the running member, then runs before older followers")]
    public async Task Restarted_failed_head_runs_right_after_the_running_member()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await DoomedAsync("orders", ct);
        var next = await Jobs.EnqueueAsync(Step("orders", "b"), ct);
        var waiting = await Jobs.EnqueueAsync(Step("orders", "c"), ct);
        await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct);
        await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(head.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(next.JobId, ct)).Status);

        var restarted = await Jobs.RestartAsync(head, "run it again", ct: ct);

        Assert.Equal(ControlAction.Applied, restarted.Action);
        Assert.Equal(JobStatusCode.Blocked, restarted.Status);
        var detail = (await Jobs.GetAsync(head, ct))!;
        Assert.Equal(0, detail.FailureCount);
        Assert.Equal(next.JobRef, detail.BlockedBehindJobRef);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, next.JobId, ct));

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(head.JobId, ct)).Status);
        var follower = (await Jobs.GetAsync(waiting, ct))!;
        Assert.Equal(JobStatusCode.Blocked, follower.Status);
        Assert.Equal(head.JobRef, follower.BlockedBehindJobRef);
    }

    [Fact(DisplayName = "A restarted finished job in a lane with no live member is Ready and runs")]
    public async Task Restarted_job_in_an_empty_lane_is_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var done = await Jobs.EnqueueAsync(Step("orders", "a"), ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, done.JobId, ct));
        var cancelled = await Jobs.EnqueueAsync(Step("billing", "b"), ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(cancelled, ct: ct)).Action);

        var restartedDone = await Jobs.RestartAsync(done, ct: ct);
        var restartedCancelled = await Jobs.RestartAsync(cancelled, ct: ct);

        Assert.Equal((ControlAction.Applied, JobStatusCode.Ready), (restartedDone.Action, restartedDone.Status));
        Assert.Equal((ControlAction.Applied, JobStatusCode.Ready), (restartedCancelled.Action, restartedCancelled.Status));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, done.JobId, ct));
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, cancelled.JobId, ct));
    }

    [Fact(DisplayName = "A restart racing the running member's completion leaves exactly one live member")]
    public async Task Restart_racing_the_head_completion_leaves_one_live_member()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < Rounds; round++)
        {
            var lane = $"race-{round}";
            var failed = await DoomedAsync(lane, ct);
            var head = await Jobs.EnqueueAsync(Step(lane, $"head-{round}"), ct);
            var waiting = await Jobs.EnqueueAsync(Step(lane, $"waiting-{round}"), ct);
            await Runtime.RunOnceAsync(TestNamespace, failed.JobId, ct);
            await Runtime.RunOnceAsync(TestNamespace, failed.JobId, ct);
            var complete = await StartedAsync(head, ct);

            var completion = complete();
            var restart = Jobs.RestartAsync(failed, "racing the head", ct: ct).AsTask();
            await Task.WhenAll(completion, restart);

            Assert.Equal(CompleteExecutionAction.Completed, (await completion).Action);
            Assert.Equal(ControlAction.Applied, (await restart).Action);
            JobStatusCode[] statuses = [(await ReadJobAsync(failed.JobId, ct)).Status, (await ReadJobAsync(waiting.JobId, ct)).Status];
            Assert.Single(statuses, s => s == JobStatusCode.Ready);
            Assert.Single(statuses, s => s == JobStatusCode.Blocked);
        }
    }

    [Fact(DisplayName = "Restarts repeated with one version reactivate the job once and then conflict")]
    public async Task Restarts_with_one_version_reactivate_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var done = await Jobs.EnqueueAsync(Step("orders", "a"), ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, done.JobId, ct));
        var version = (await Jobs.GetAsync(done, ct))!.Version;

        var outcomes = await Task.WhenAll(
            Jobs.RestartAsync(done, expectedVersion: version, ct: ct).AsTask(),
            Jobs.RestartAsync(done, expectedVersion: version, ct: ct).AsTask()
        );
        var again = await Jobs.RestartAsync(done, expectedVersion: version, ct: ct);

        var applied = Assert.Single(outcomes, o => o.Action == ControlAction.Applied);
        Assert.Single(outcomes, o => o.Action == ControlAction.VersionConflict);
        Assert.Equal(ControlAction.VersionConflict, again.Action);
        Assert.Equal(version + 1, applied.Version);
        Assert.Equal(version + 1, (await Jobs.GetAsync(done, ct))!.Version);
    }

    [Fact(DisplayName = "A restarted laned child keeps its parent, and the ancestor guard sees it unfinished again")]
    public async Task Restarted_laned_child_keeps_its_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(Step("", "p"), ct);
        var child = await Jobs.EnqueueAsync(Step("orders", "c") with { ParentJobId = parent.JobId }, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, child.JobId, ct));

        var restarted = await Jobs.RestartAsync(child, ct: ct);

        Assert.Equal((ControlAction.Applied, JobStatusCode.Ready), (restarted.Action, restarted.Status));
        Assert.Equal(parent.JobRef, (await Jobs.GetAsync(child, ct))!.ParentJobRef);
        var rejected = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(Step("orders", "gc") with { ParentJobId = child.JobId }, ct)
        );
        Assert.Equal(EnqueueRejectionReason.AncestorLane, rejected.Reason);
        var elsewhere = await Jobs.EnqueueAsync(Step("billing", "gc2") with { ParentJobId = child.JobId }, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(elsewhere.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A finished job with an unfinished descendant in its lane is not restarted")]
    public async Task Restart_refuses_a_job_with_an_unfinished_descendant_in_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        var ancestor = await Jobs.EnqueueAsync(Step("orders", "p"), ct);
        var middle = await Jobs.EnqueueAsync(Step("", "c") with { ParentJobId = ancestor.JobId }, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, ancestor.JobId, ct));
        var head = await Jobs.EnqueueAsync(Step("orders", "x"), ct);
        var descendant = await Jobs.EnqueueAsync(Step("orders", "d") with { ParentJobId = middle.JobId }, ct);
        Assert.Equal(JobStatusCode.Blocked, (await ReadJobAsync(descendant.JobId, ct)).Status);
        var finished = (await Jobs.GetAsync(ancestor, ct))!;

        var restarted = await Jobs.RestartAsync(ancestor, ct: ct);

        Assert.Equal((ControlAction.Rejected, JobStatusCode.Succeeded), (restarted.Action, restarted.Status));
        Assert.Equal(finished.Version, (await Jobs.GetAsync(ancestor, ct))!.Version);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(descendant.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A finished job with an unfinished ancestor in its lane is not restarted")]
    public async Task Restart_refuses_a_job_with_an_unfinished_ancestor_in_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        var ancestor = await Jobs.EnqueueAsync(Step("orders", "p"), ct);
        var middle = await Jobs.EnqueueAsync(Step("", "c") with { ParentJobId = ancestor.JobId }, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, ancestor.JobId, ct));
        var descendant = await Jobs.EnqueueAsync(Step("orders", "d") with { ParentJobId = middle.JobId }, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, descendant.JobId, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(ancestor, ct: ct)).Action);

        var restarted = await Jobs.RestartAsync(descendant, ct: ct);

        Assert.Equal((ControlAction.Rejected, JobStatusCode.Succeeded), (restarted.Action, restarted.Status));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(descendant.JobId, ct)).Status);
    }

    // Claims and starts the job as this namespace's worker and hands back its completion, unsent.
    private async Task<Func<Task<CompleteExecutionResult>>> StartedAsync(JobEnqueueOutcome job, CancellationToken ct)
    {
        var store = Services.GetRequiredService<IExecutionStore>();
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        var workerId = Assert.IsType<JobWorker>(worker).Id;
        var claimed = Assert.Single(await store.ClaimOneAsync(ns, workerId, leaseTtl, job.JobId, ct));
        Assert.Equal(
            StartExecutionAction.Started,
            await store.StartExecutionAsync(claimed.JobId, workerId, claimed.ExecutionNumber, claimed.Version, leaseTtl, ct)
        );
        return () =>
            store.CompleteExecutionAsync(
                new CompleteExecutionRequest(
                    JobId: claimed.JobId,
                    WorkerId: workerId,
                    ExpectedExecutionNumber: claimed.ExecutionNumber,
                    Outcome: ExecutionOutcome.Succeeded,
                    ResultFormatId: 0,
                    Result: ReadOnlyMemory<byte>.Empty
                ),
                ct
            );
    }

    private async Task<JobEnqueueOutcome> DoomedAsync(string lane, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "lane-doomed", JobPayload.Json(new LaneDoomedStep(lane, "doomed")), Lane: lane),
            ct
        );

    private JobEnqueueRequest Step(string lane, string label) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), Lane: lane.Length == 0 ? null : lane);
}
