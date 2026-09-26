using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// The relay claims staged rows in staging order and enqueues them in that order, so a lane staged
/// through the outbox runs in the order its rows were staged. Priority reorders nothing on the way; it
/// still applies to the enqueued jobs.
/// </summary>
[ConformanceSpec(
    "outbox.lane-order",
    "Rows staged into one lane relay in staging order whatever their priority",
    Area = "Outbox",
    Contract = "The relay claims staged rows in staging order and enqueues them in that order, so a lane's rows keep their staging order however their priorities differ.",
    Arrange = "A low-priority row and then a high-priority row are staged into one lane in one producer transaction.",
    Act = "One relay tick relays the source.",
    Assert = "The low-priority row's job leads the lane Ready and the high-priority row's job waits Blocked behind it."
)]
public abstract class OutboxLaneOrderSpec<TFixture> : OutboxRelayIntegrationBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A low-priority row staged first leads its lane ahead of a high-priority row staged after it")]
    public async Task Staging_order_wins_over_priority()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.StageInOneTransactionAsync(
            SourceTable,
            [Step("first-" + TestId, JobPriorityCode.Bulk), Step("second-" + TestId, JobPriorityCode.Critical)]
        );

        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        var first = await JobAsync("first-" + TestId, ct);
        var second = await JobAsync("second-" + TestId, ct);
        Assert.True(first.JobId < second.JobId, "the row staged first was enqueued second");
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(first.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Blocked, (await ReadJobAsync(second.JobId, ct)).Status);
    }

    private JobEnqueueRequest Step(string dedup, JobPriorityCode priority) =>
        new(
            TestNamespace,
            "lane-step",
            JobPayload.Json(new LaneStep("staged", dedup)),
            DeduplicationKey: dedup,
            Priority: priority,
            Lane: "staged"
        );

    private async Task<(long JobId, string Dedup)> JobAsync(string dedup, CancellationToken ct)
    {
        var job = (await Db.From<Job>().Where(j => j.NamespaceId == NamespaceId && j.DeduplicationKey == dedup).SingleOrDefaultAsync(ct))!;
        return (job.Id, dedup);
    }
}
