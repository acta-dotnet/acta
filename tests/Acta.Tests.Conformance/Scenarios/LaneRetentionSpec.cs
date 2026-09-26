using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Retention deletes a lane once no runtime row references it, finished or not, so lanes named per
/// customer or per order do not accumulate. An enqueue that races the delete still lands in a lane.
/// </summary>
[ConformanceSpec(
    "lanes.retention",
    "Retention deletes a lane that no runtime references",
    Area = "Lanes",
    Contract = "A retention sweep deletes a lane that no runtime row references, keeps a lane with any runtime row, and never strands an enqueue racing the delete.",
    Arrange = "One lane lost its only job to a purge, one holds a finished job, one holds a live job, and more lanes are emptied for an enqueue race.",
    Act = "Retention sweeps the namespace, alone and alongside enqueues into the emptied lanes.",
    Assert = "Only the unreferenced lane is gone, and every racing enqueue lands in a lane row that exists with its job Ready."
)]
public abstract class LaneRetentionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int NoEventPurgeDays = 100_000;
    private const int NoAlertPurgeDays = 100_000;
    private const int NoWorkerPurgeSeconds = 100_000_000;
    private const int RaceRounds = 20;

    [Fact(DisplayName = "An unreferenced lane is deleted while lanes with a finished or live job survive")]
    public async Task Unreferenced_lane_is_deleted_and_referenced_lanes_survive()
    {
        var ct = TestContext.Current.CancellationToken;
        await EmptiedLaneAsync("gone", ct);
        var finished = await StepAsync("finished", ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(finished, ct: ct)).Action);
        await StepAsync("live", ct);

        await RetentionTestOps.PurgeUntilAsync(
            Services,
            Runtime.RegisteredNamespaceIds[TestNamespace],
            NoEventPurgeDays,
            NoAlertPurgeDays,
            NoWorkerPurgeSeconds,
            1000,
            50,
            async () => !await LaneExistsAsync("gone", ct),
            ct
        );

        Assert.False(await LaneExistsAsync("gone", ct));
        Assert.True(await LaneExistsAsync("finished", ct));
        Assert.True(await LaneExistsAsync("live", ct));
    }

    [Fact(DisplayName = "An enqueue racing the delete of its emptied lane lands in an existing lane")]
    public async Task Enqueue_racing_the_delete_lands_in_a_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < RaceRounds; round++)
        {
            var lane = $"race-{round}";
            await EmptiedLaneAsync(lane, ct);

            var sweep = RetentionTestOps.PurgeAsync(
                Services,
                Runtime.RegisteredNamespaceIds[TestNamespace],
                NoEventPurgeDays,
                NoAlertPurgeDays,
                NoWorkerPurgeSeconds,
                1000,
                1,
                ct
            );
            var enqueue = StepAsync(lane, ct);
            await Task.WhenAll(sweep, enqueue);

            var job = await Jobs.GetAsync(await enqueue, ct);
            Assert.Equal(lane, job!.Lane);
            Assert.Equal(JobStatusCode.Ready, job.Status);
            Assert.True(await LaneExistsAsync(lane, ct));
        }
    }

    // A lane whose only job was cancelled and purged, so no runtime row references it.
    private async Task EmptiedLaneAsync(string lane, CancellationToken ct)
    {
        var job = await StepAsync(lane, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(job, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.PurgeAsync(job, ct: ct)).Action);
        Assert.True(await LaneExistsAsync(lane, ct));
    }

    private async Task<bool> LaneExistsAsync(string lane, CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        return await Db.From<JobLane>().Where(l => l.NamespaceId == ns && l.Name == lane).SingleOrDefaultAsync(ct) is not null;
    }

    private async Task<JobEnqueueOutcome> StepAsync(string lane, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, lane)), Lane: lane),
            ct
        );
}
