using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// The read side names a job's lane and, for a Blocked member, the lane member it waits behind; the
/// jobs list filters by lane under the same case folding as the lane name itself.
/// </summary>
[ConformanceSpec(
    "lanes.read",
    "Job reads name the lane and the member a Blocked job waits behind",
    Area = "Lanes",
    Contract = "A job read carries its lane and, while Blocked, the lane's lowest-id unfinished member, and the jobs list filters by lane.",
    Arrange = "Two lanes and an unlaned job are enqueued into a private namespace, so each lane holds a head and a Blocked follower.",
    Act = "Each job is read, and the namespace's jobs are listed with a lane filter spelled in another case.",
    Assert = "Laned jobs carry their lane, only the follower names its head, and the filtered list holds exactly that lane's jobs."
)]
public abstract class LaneReadSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A Blocked follower reads its lane and the head it waits behind")]
    public async Task Blocked_follower_reads_its_lane_and_head()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("orders", "a", ct);
        var follower = await StepAsync("orders", "b", ct);
        var loose = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep("", "x"))), ct);

        var headRead = await Jobs.GetAsync(head, ct);
        var followerRead = await Jobs.GetAsync(follower, ct);
        var looseRead = await Jobs.GetAsync(loose, ct);

        Assert.Equal("orders", headRead!.Lane);
        Assert.Null(headRead.BlockedBehindJobRef);
        Assert.Equal("orders", followerRead!.Lane);
        Assert.Equal(JobStatusCode.Blocked, followerRead.Status);
        Assert.Equal(head.JobRef, followerRead.BlockedBehindJobRef);
        Assert.Equal(head.JobId, followerRead.BlockedBehindJobId);
        Assert.Null(looseRead!.Lane);
        Assert.Null(looseRead.BlockedBehindJobRef);
    }

    [Fact(DisplayName = "The jobs list filters by lane, folding the filter's case")]
    public async Task Jobs_list_filters_by_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("orders", "a", ct);
        var follower = await StepAsync("orders", "b", ct);
        await StepAsync("billing", "c", ct);
        await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep("", "x"))), ct);

        var page = await Operations.Ledger.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace, Lane: "Orders"), ct);

        Assert.Equal([follower.JobRef, head.JobRef], page.Items.Select(i => i.JobRef));
        Assert.All(page.Items, i => Assert.Equal("orders", i.Lane));
    }

    private async Task<JobEnqueueOutcome> StepAsync(string lane, string label, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), Lane: lane),
            ct
        );
}
