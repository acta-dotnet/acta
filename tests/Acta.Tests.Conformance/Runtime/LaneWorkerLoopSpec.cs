using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Lanes under a real worker loop with several executors: however many executors claim at once, one
/// member of a lane runs at a time and the lane drains in enqueue order, while different lanes run side
/// by side. The profile is set by the concrete class, so the same contract holds for the Direct loop
/// and for the Bulk loop, whose batch completion is the settle that promotes the next member.
/// </summary>
[ConformanceSpec(
    "lanes.worker-loop",
    "A worker loop with several executors keeps one member of a lane in flight",
    Area = "Lanes",
    Contract = "Under a worker loop with several executors each lane runs one member at a time in enqueue order while different lanes run in parallel.",
    Arrange = "Two lanes of probe jobs are enqueued in one batch into a private namespace served by a multi-executor worker.",
    Act = "The worker loop drains the namespace.",
    Assert = "Every probe ran once, each lane in enqueue order, and no lane ever had two probes running at once."
)]
public abstract class LaneWorkerLoopSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int PerLane = 6;

    /// <summary>The execution profile the worker loop runs under.</summary>
    protected abstract ExecutionProfile Profile { get; }

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = Profile;
            o.MaxConcurrentExecutors = 6;
        });
    }

    [Fact(DisplayName = "Each lane drains in order with one member in flight while lanes run in parallel")]
    public async Task Each_lane_drains_in_order_with_one_member_in_flight()
    {
        var ct = TestContext.Current.CancellationToken;
        var batch = new List<JobEnqueueRequest>();
        for (var i = 1; i <= PerLane; i++)
        {
            batch.Add(Step("orders", $"o{i}"));
            batch.Add(Step("billing", $"b{i}"));
        }
        var outcomes = await Jobs.EnqueueBatchAsync(batch, ct);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        while (LaneProbes.Ran(TestNamespace).Count < outcomes.Count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, ct);
        }
        await loopCts.CancelAsync();
        await loop;

        var ran = LaneProbes.Ran(TestNamespace);
        Assert.Equal(outcomes.Count, ran.Count);
        Assert.Equal(Enumerable.Range(1, PerLane).Select(i => $"o{i}"), ran.Where(l => l[0] == 'o'));
        Assert.Equal(Enumerable.Range(1, PerLane).Select(i => $"b{i}"), ran.Where(l => l[0] == 'b'));
        Assert.Equal(1, LaneProbes.MaxConcurrent(TestNamespace));
    }

    private JobEnqueueRequest Step(string lane, string label) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), Lane: lane);
}
