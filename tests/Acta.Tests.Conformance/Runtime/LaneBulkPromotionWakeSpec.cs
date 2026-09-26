using System.Diagnostics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Under the Bulk profile a lane's next member is promoted by the batch completion, not by the worker
/// that ran the head, so that completion has to wake the claim loop itself. With a long safety poll an
/// unannounced promotion would leave each member waiting out the poll.
/// </summary>
[ConformanceSpec(
    "lanes.bulk-promotion-wake",
    "A Bulk batch completion that promotes a lane member wakes the claim loop",
    Area = "Lanes",
    Contract = "Under the Bulk profile a batch completion that promotes a lane's next member wakes the namespace's claim loop, so the lane drains at flush pace.",
    Arrange = "A lane of ten no-op probes sits in a private namespace served by an otherwise idle Bulk worker with a sixty-second safety poll.",
    Act = "The worker loop drains the lane.",
    Assert = "All ten probes ran in order within thirty seconds, half of the single safety poll one unannounced promotion would cost."
)]
public abstract class LaneBulkPromotionWakeSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int Members = 10;
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(60);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = ExecutionProfile.Bulk;
            o.SafetyPollInterval = SafetyPoll;
        });
    }

    [Fact(DisplayName = "A Bulk lane of ten no-op jobs drains at flush pace, not at safety-poll pace")]
    public async Task Bulk_lane_drains_at_flush_pace()
    {
        var ct = TestContext.Current.CancellationToken;
        // One missed wake costs a whole minute; half of one leaves a slow runner three seconds a member.
        var budget = SafetyPoll / 2;
        await Jobs.EnqueueBatchAsync(
            [
                .. Enumerable
                    .Range(1, Members)
                    .Select(i => new JobEnqueueRequest(
                        TestNamespace,
                        "lane-step",
                        JobPayload.Json(new LaneStep("wake", $"w{i}")),
                        Lane: "wake"
                    )),
            ],
            ct
        );

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        while (LaneProbes.Ran(TestNamespace).Count < Members && clock.Elapsed < budget + SafetyPoll)
        {
            await Task.Delay(25, ct);
        }
        var elapsed = clock.Elapsed;
        await loopCts.CancelAsync();
        await loop;

        Assert.Equal(Enumerable.Range(1, Members).Select(i => $"w{i}"), LaneProbes.Ran(TestNamespace));
        Assert.True(
            elapsed <= budget,
            $"the lane took {elapsed.TotalMilliseconds:F0} ms against a {budget.TotalMilliseconds:F0} ms budget"
        );
    }
}
