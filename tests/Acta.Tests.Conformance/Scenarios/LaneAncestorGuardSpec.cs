using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// A child cannot join the lane of an unfinished ancestor: it would wait Blocked behind the ancestor
/// that waits for it. The guard walks the whole lineage and reads the effective lane (the enqueue's,
/// else the definition's), and a child never inherits its parent's lane.
/// </summary>
[ConformanceSpec(
    "lanes.ancestor-guard",
    "A child in the lane of an unfinished ancestor is rejected",
    Area = "Lanes",
    Contract = "An enqueue whose effective lane equals the lane of an unfinished ancestor is rejected with AncestorLane, and a child never inherits its parent's lane.",
    Arrange = "An unfinished laned parent, an unlaned child of it, and a laned definition parent exist in a private namespace.",
    Act = "Children and grandchildren are enqueued single and batched into the ancestors' lanes, another lane, and no lane.",
    Assert = "Every enqueue into an unfinished ancestor's lane is rejected with AncestorLane and every other enqueue lands."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class LaneAncestorGuardSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A child in its parent's lane is rejected, in another lane or none it lands, and it inherits no lane")]
    public async Task Child_in_its_parents_lane_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(Step("orders", "p", lane: "orders"), ct);

        var rejected = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(Step("orders", "c", lane: "orders") with { ParentJobId = parent.JobId }, ct)
        );
        Assert.Equal(EnqueueRejectionReason.AncestorLane, rejected.Reason);

        var other = await Jobs.EnqueueAsync(Step("billing", "c2", lane: "billing") with { ParentJobId = parent.JobId }, ct);
        var unlaned = await Jobs.EnqueueAsync(Step("none", "c3", lane: null) with { ParentJobId = parent.JobId }, ct);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(other.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(unlaned.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A grandchild in its grandparent's lane is rejected, single and batched")]
    public async Task Grandchild_in_its_grandparents_lane_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var grandparent = await Jobs.EnqueueAsync(Step("orders", "g", lane: "orders"), ct);
        var parent = await Jobs.EnqueueAsync(Step("none", "p", lane: null) with { ParentJobId = grandparent.JobId }, ct);

        var single = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(Step("orders", "gc", lane: "orders") with { ParentJobId = parent.JobId }, ct)
        );
        Assert.Equal(EnqueueRejectionReason.AncestorLane, single.Reason);

        var batched = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueBatchAsync(
                [
                    Step("billing", "fine", lane: "billing") with
                    {
                        ParentJobId = parent.JobId,
                    },
                    Step("orders", "gc2", lane: "orders") with
                    {
                        ParentJobId = parent.JobId,
                    },
                ],
                ct
            )
        );
        Assert.Equal(EnqueueRejectionReason.AncestorLane, batched.Reason);
    }

    [Fact(DisplayName = "A child whose definition lane equals its unfinished parent's lane is rejected")]
    public async Task Child_inheriting_the_same_definition_lane_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(Defined("p", lane: null), ct);

        var rejected = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(Defined("c", lane: null) with { ParentJobId = parent.JobId }, ct)
        );
        Assert.Equal(EnqueueRejectionReason.AncestorLane, rejected.Reason);

        var overridden = await Jobs.EnqueueAsync(Defined("c2", lane: "elsewhere") with { ParentJobId = parent.JobId }, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(overridden.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A finished ancestor's lane is open to its descendants")]
    public async Task Finished_ancestor_lane_is_open()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(Step("none", "p", lane: null), ct);
        var laned = await Jobs.EnqueueAsync(Step("orders", "c", lane: "orders") with { ParentJobId = parent.JobId }, ct);
        await Jobs.CancelAsync(laned, ct: ct);

        var again = await Jobs.EnqueueAsync(Step("orders", "c2", lane: "orders") with { ParentJobId = parent.JobId }, ct);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(again.JobId, ct)).Status);
    }

    private JobEnqueueRequest Step(string probeLane, string label, string? lane) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(probeLane, label)), Lane: lane);

    private JobEnqueueRequest Defined(string label, string? lane) =>
        new(TestNamespace, "lane-defined", JobPayload.Json(new LaneDefinedStep(label)), Lane: lane);
}
