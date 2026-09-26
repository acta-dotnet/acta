using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Every path that settles a laned head hands the lane to its next member: a reclaim that fails the
/// head for good, a definition retire that cancels it, and the Bulk profile's batch completion. A
/// scalar completion that promotes a member also wakes the namespace's claim loops.
/// </summary>
[ConformanceSpec(
    "lanes.settle-paths",
    "Every settle of a lane head promotes the next member",
    Area = "Lanes",
    Contract = "A laned head settled by reclaim, retire, or batch completion hands its lane to the next member, and a promoting completion wakes the namespace.",
    Arrange = "Laned heads with Blocked followers sit in a private namespace, one lane per path.",
    Act = "The head is reclaimed to Failed, cancelled by a definition retire, completed under Bulk, or completed with a recording wakeup.",
    Assert = "The follower is Ready or has run after each settle, and the promoting completion published a worker-namespace wake."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ReclaimStuckJobsAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class LaneSettlePathsSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private readonly RecordingWakeup _wakeups = new();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Registered ahead of UseActa's TryAddSingleton, so the runtime publishes through the recorder.
        services.AddSingleton<IWorkerWakeup>(_wakeups);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A head reclaimed to Failed promotes the next member")]
    public async Task Head_reclaimed_to_failed_promotes_the_next_member()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await Jobs.EnqueueAsync(Request("lane-doomed", new LaneDoomedStep("orders", "d"), "orders"), ct);
        var next = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "b"), "orders"), ct);

        // One attempt already spent of lane-doomed's two, and its lease long gone: the reclaim charges
        // the second and fails the head for good.
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = 50, leased_by_worker_id = @p_worker, "
                + "lease_expires_at_utc = @p_expires, failure_count = 1 WHERE job_id = @p_id",
            ct,
            ("@p_worker", RuntimeStateStaging.StagedWorkerId),
            ("@p_expires", DateTime.UtcNow.AddMinutes(-5)),
            ("@p_id", head.JobId)
        );

        await RecoverySweep.ReclaimAtLeastOneAsync(Services, Runtime.RegisteredNamespaceIds[TestNamespace], ct);

        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(head.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(next.JobId, ct)).Status);
    }

    [Fact(DisplayName = "Retiring the head's definition cancels the head and promotes a member of another definition")]
    public async Task Retire_cancels_the_head_and_promotes_the_next_member()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "a"), "orders"), ct);
        var next = await Jobs.EnqueueAsync(Request("lane-defined", new LaneDefinedStep("b"), "orders"), ct);
        Assert.Equal(JobStatusCode.Blocked, (await ReadJobAsync(next.JobId, ct)).Status);

        var definition = await Operations.Definitions.GetAsync(TestNamespace, "lane-step", ct);
        Assert.NotNull(definition);
        var retired = await Operations.Definitions.RetireAsync(TestNamespace, "lane-step", definition!.Version, "tester", "retired", ct);

        Assert.Equal(ControlAction.Applied, retired.Action);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(head.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(next.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A completion that promotes a member wakes the namespace's claim loops")]
    public async Task Promoting_completion_wakes_the_namespace()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "a"), "orders"), ct);
        await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "b"), "orders"), ct);
        var before = _wakeups.Published.Count;

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));

        var published = _wakeups.Published.Skip(before).ToList();
        Assert.Contains(published, c => c.Kind == WorkerWakeupChannelKind.WorkerNamespace);
    }

    [Fact(DisplayName = "A completion that promotes nothing wakes no claim loop")]
    public async Task Completion_without_a_follower_wakes_no_claim_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "a"), "orders"), ct);
        var before = _wakeups.Published.Count;

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));

        Assert.DoesNotContain(_wakeups.Published.Skip(before), c => c.Kind == WorkerWakeupChannelKind.WorkerNamespace);
    }

    [Fact(DisplayName = "Cancelling a Blocked follower wakes no claim loop, and cancelling a head that promotes one does")]
    public async Task Cancel_wakes_claim_loops_only_on_a_promotion()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "a"), "orders"), ct);
        var follower = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "b"), "orders"), ct);
        await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "c"), "orders"), ct);

        var before = _wakeups.Published.Count;
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(follower, ct: ct)).Action);
        Assert.DoesNotContain(_wakeups.Published.Skip(before), IsWorkAvailable);

        before = _wakeups.Published.Count;
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(head, ct: ct)).Action);
        Assert.Contains(_wakeups.Published.Skip(before), IsWorkAvailable);
    }

    [Fact(DisplayName = "A cancel whose descendant cascade promotes a lane member wakes the claim loops")]
    public async Task Cascade_cancel_that_promotes_wakes_claim_loops()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("", "p"), lane: null), ct);
        await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "child"), "orders") with { ParentJobId = parent.JobId }, ct);
        var waiting = await Jobs.EnqueueAsync(Request("lane-step", new LaneStep("orders", "w"), "orders"), ct);
        Assert.Equal(JobStatusCode.Blocked, (await ReadJobAsync(waiting.JobId, ct)).Status);

        var before = _wakeups.Published.Count;
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(parent, ct: ct)).Action);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(waiting.JobId, ct)).Status);
        Assert.Contains(_wakeups.Published.Skip(before), IsWorkAvailable);
    }

    private static bool IsWorkAvailable(WorkerWakeupChannel channel) =>
        channel.Kind is WorkerWakeupChannelKind.WorkerNamespace or WorkerWakeupChannelKind.AllWorkerNamespaces;

    private JobEnqueueRequest Request<TInput>(string jobName, TInput input, string? lane)
        where TInput : notnull => new(TestNamespace, jobName, JobPayload.Json(input), Lane: lane);
}
