using System.Text;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Restarting a finished laned job cannot reopen it in place, which would put it ahead of members that
/// already ran after it; it redrives the job instead, as a new job entering the lane at its tail. The
/// copy, the version bump on the finished row, and both events commit together or not at all.
/// </summary>
[ConformanceSpec(
    "lanes.redrive",
    "Restarting a finished laned job redrives it at the lane's tail",
    Area = "Lanes",
    Contract = "A restart of a finished laned job enqueues a copy at its lane's tail, bumps the finished row's version, links both by events, and returns the new job.",
    Arrange = "A laned head has run to Succeeded while a follower behind it is still unfinished.",
    Act = "An operator restarts the finished head.",
    Assert = "A new job with the same definition, input, lane, and priority waits behind the follower, the old row stays Succeeded, and each row's event names the other."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RedriveJobAsync))]
public abstract class LaneRedriveSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A restarted finished head is redriven behind the lane's unfinished follower")]
    public async Task Restarted_head_is_redriven_at_the_tail()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", JobPriorityCode.High, ct);
        var follower = await StepAsync("b", null, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));
        var finished = (await Jobs.GetAsync(head, ct))!;

        var restarted = await Jobs.RestartAsync(head, "run it again", ct: ct);

        Assert.Equal(ControlAction.Redriven, restarted.Action);
        Assert.Equal(JobStatusCode.Succeeded, restarted.Status);
        Assert.Equal(finished.Version + 1, restarted.Version);
        var redriveRef = Assert.NotNull(restarted.RedriveJobRef);
        var after = (await Jobs.GetAsync(head, ct))!;
        Assert.Equal(JobStatusCode.Succeeded, after.Status);
        Assert.Equal(restarted.Version, after.Version);

        var redriven = (await Jobs.GetAsync(JobLookup.ByRef(redriveRef), ct))!;
        Assert.Equal(restarted.RedriveJobId, redriven.JobId);
        Assert.Equal("lane-step", redriven.JobName);
        Assert.Equal("orders", redriven.Lane);
        Assert.Equal(JobPriorityCode.High, redriven.Priority);
        Assert.Equal(JobStatusCode.Blocked, redriven.Status);
        Assert.Equal(follower.JobRef, redriven.BlockedBehindJobRef);
        var input = Assert.NotNull(await Jobs.GetInputAsync(JobLookup.ByRef(redriveRef), ct));
        var original = Assert.NotNull(await Jobs.GetInputAsync(head, ct));
        Assert.Equal(original.Format, input.Format);
        Assert.Equal(original.Data.ToArray(), input.Data.ToArray());

        Assert.Contains(redriveRef.ToString(), await RedriveDetailAsync(head.JobId, ct));
        Assert.Contains(head.JobRef.ToString(), await RedriveDetailAsync(redriven.JobId, ct));
    }

    [Fact(DisplayName = "A restarted finished job in an idle lane is redriven Ready")]
    public async Task Restarted_job_in_an_idle_lane_is_redriven_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var only = await StepAsync("a", null, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, only.JobId, ct));

        var restarted = await Jobs.RestartAsync(only, ct: ct);

        var redriven = await Jobs.GetAsync(JobLookup.ByRef(Assert.NotNull(restarted.RedriveJobRef)), ct);
        Assert.Equal(JobStatusCode.Ready, redriven!.Status);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, redriven.JobId, ct));
    }

    [Fact(DisplayName = "A failed laned head is redriven and keeps its Failed row")]
    public async Task Failed_head_is_redriven()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "lane-doomed", JobPayload.Json(new LaneDoomedStep("orders", "d")), Lane: "orders"),
            ct
        );
        await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct);
        await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(head.JobId, ct)).Status);

        var restarted = await Jobs.RestartAsync(head, ct: ct);

        Assert.Equal(ControlAction.Redriven, restarted.Action);
        Assert.Equal(JobStatusCode.Failed, restarted.Status);
        var redriven = (await Jobs.GetAsync(JobLookup.ByRef(Assert.NotNull(restarted.RedriveJobRef)), ct))!;
        Assert.Equal("lane-doomed", redriven.JobName);
        Assert.Equal(JobStatusCode.Ready, redriven.Status);
    }

    [Fact(DisplayName = "A cancelled laned head is redriven and keeps its Cancelled row")]
    public async Task Cancelled_head_is_redriven()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", null, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(head, ct: ct)).Action);

        var restarted = await Jobs.RestartAsync(head, ct: ct);

        Assert.Equal(ControlAction.Redriven, restarted.Action);
        Assert.Equal(JobStatusCode.Cancelled, restarted.Status);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(head.JobId, ct)).Status);
        var redriven = (await Jobs.GetAsync(JobLookup.ByRef(Assert.NotNull(restarted.RedriveJobRef)), ct))!;
        Assert.Equal(JobStatusCode.Ready, redriven.Status);
    }

    [Fact(DisplayName = "A restart with a stale version is a conflict and enqueues nothing")]
    public async Task Stale_version_conflicts_without_a_copy()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", null, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));
        var finished = (await Jobs.GetAsync(head, ct))!;

        var restarted = await Jobs.RestartAsync(head, expectedVersion: finished.Version - 1, ct: ct);

        Assert.Equal(ControlAction.VersionConflict, restarted.Action);
        Assert.Equal(JobStatusCode.Succeeded, restarted.Status);
        Assert.Equal(finished.Version, restarted.Version);
        Assert.Null(restarted.RedriveJobRef);
        Assert.Equal(1, await JobCountAsync(ct));
    }

    [Fact(DisplayName = "Two restarts carrying one version redrive the job exactly once")]
    public async Task Restarts_with_one_version_redrive_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", null, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));
        var version = (await Jobs.GetAsync(head, ct))!.Version;

        var outcomes = await Task.WhenAll(
            Jobs.RestartAsync(head, expectedVersion: version, ct: ct).AsTask(),
            Jobs.RestartAsync(head, expectedVersion: version, ct: ct).AsTask()
        );
        var again = await Jobs.RestartAsync(head, expectedVersion: version, ct: ct);

        Assert.Single(outcomes, o => o.Action == ControlAction.Redriven);
        Assert.Single(outcomes, o => o.Action == ControlAction.VersionConflict);
        Assert.Equal(ControlAction.VersionConflict, again.Action);
        Assert.Equal(2, await JobCountAsync(ct));
    }

    [Fact(DisplayName = "A retired definition refuses the redrive and leaves the finished row untouched")]
    public async Task Retired_definition_refuses_the_redrive()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", null, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));
        var definition = (await Operations.Definitions.GetAsync(TestNamespace, "lane-step", ct))!;
        await Operations.Definitions.RetireAsync(TestNamespace, "lane-step", definition.Version, ct: ct);
        var finished = (await Jobs.GetAsync(head, ct))!;

        var restarted = await Jobs.RestartAsync(head, ct: ct);

        Assert.Equal(ControlAction.Rejected, restarted.Action);
        Assert.Equal(JobStatusCode.Succeeded, restarted.Status);
        Assert.Null(restarted.RedriveJobRef);
        Assert.Equal(finished.Version, (await Jobs.GetAsync(head, ct))!.Version);
        Assert.Equal(1, await JobCountAsync(ct));
    }

    [Fact(DisplayName = "A redriven child keeps its tags and drops its dedup key and parent")]
    public async Task Redriven_child_keeps_tags_and_drops_dedup_key_and_parent()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep("", "p"))), ct);
        var child = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "lane-step",
                JobPayload.Json(new LaneStep("orders", "c")),
                DeduplicationKey: "child-" + TestId,
                Tags: [new TagInput("customer", "acme"), new TagInput("rush")],
                ParentJobId: parent.JobId,
                Lane: "orders"
            ),
            ct
        );
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, child.JobId, ct));

        var restarted = await Jobs.RestartAsync(child, ct: ct);

        Assert.Equal(ControlAction.Redriven, restarted.Action);
        var copyRef = Assert.NotNull(restarted.RedriveJobRef);
        var copy = (await Jobs.GetAsync(JobLookup.ByRef(copyRef), ct))!;
        Assert.Null(copy.ParentJobRef);
        Assert.Null(copy.DeduplicationKey);
        Assert.Equal("orders", copy.Lane);
        var tags = await Operations.Tags.GetAsync(TagTarget.ForJob(JobLookup.ByRef(copyRef)), ct);
        Assert.NotNull(tags);
        Assert.Equal([new TagItem("customer", "acme"), new TagItem("rush")], tags.OrderBy(static t => t.Name));
    }

    private async Task<int> JobCountAsync(CancellationToken ct)
    {
        var page = await Operations.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: TestNamespace, Lane: "orders", IncludeTotal: true),
            ct
        );
        return (int)page.TotalCount!;
    }

    private async Task<string> RedriveDetailAsync(long jobId, CancellationToken ct)
    {
        var events = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == EventCode.JobRedriven).ToListAsync(ct);
        return Encoding.UTF8.GetString(Assert.Single(events).Detail ?? []);
    }

    private async Task<JobEnqueueOutcome> StepAsync(string label, JobPriorityCode? priority, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "lane-step",
                JobPayload.Json(new LaneStep("orders", label)),
                Priority: priority,
                Lane: "orders"
            ),
            ct
        );
}
