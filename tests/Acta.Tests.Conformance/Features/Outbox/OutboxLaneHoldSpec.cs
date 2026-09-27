using Acta.Relational.Entities;
using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// A lane keeps its staging order from the outbox to the ledger. A laned row is claimed only while no
/// older row of its lane is still Pending or Claimed, so a rejected row holds the rows behind it, a
/// claim another relay holds does too, and a quarantined row lets its lane move on. Other lanes flow.
/// </summary>
[ConformanceSpec(
    "outbox.lane-hold",
    "An outbox lane waits behind its oldest waiting row until that row leaves",
    Area = "Outbox",
    Contract = "A laned outbox row is claimed only while no older row of its lane is Pending or Claimed, so a lane reaches the ledger in staging order.",
    Arrange = "Rows are staged into lanes behind a row the target rejects, behind a row another claim holds, and in one long lane.",
    Act = "Relay ticks run, with a quarantine threshold of one where the spec quarantines the rejected row.",
    Assert = "A lane's later rows stay in the outbox while an older row waits, other lanes relay, quarantine releases the lane, and a long lane lands in staging order."
)]
public abstract class OutboxLaneHoldSpec<TFixture> : OutboxRelayIntegrationBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A rejected row holds its lane across ticks while another lane relays")]
    public async Task A_rejected_row_holds_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.StageInOneTransactionAsync(
            SourceTable,
            [Row("held", "a", jobName: "no-such-job"), Row("held", "b"), Row("free", "c")]
        );

        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        await Fixture.RewindOutboxAsync(SourceTable);
        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal(1, await CountLedgerJobsAsync(Key("c"), ct));
        Assert.Equal(0, await CountLedgerJobsAsync(Key("b"), ct));
        Assert.Equal(2, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A quarantined row releases its lane and the row behind it relays")]
    public async Task A_quarantined_row_releases_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.StageInOneTransactionAsync(SourceTable, [Row("dead", "a", jobName: "no-such-job"), Row("dead", "b")]);

        await Assert.ThrowsAsync<OutboxQuarantineTickException>(() =>
            Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(quarantineThreshold: 1), ct)
        );

        Assert.Equal(1, await CountLedgerJobsAsync(Key("b"), ct));
    }

    [Fact(DisplayName = "A lane's later rows wait while another claim holds its head")]
    public async Task A_claimed_head_holds_its_lane()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.StageInOneTransactionAsync(SourceTable, [Row("claimed", "a"), Row("claimed", "b")]);

        // Another relay's claim, left in flight: the head is Claimed under a token this tick does not own.
        var held = await SourceStore.ClaimDueAsync(new ClaimOutboxCommand(Guid.NewGuid(), 10, 180), ct);
        Assert.Single(held);

        await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

        Assert.Equal(0, await CountLedgerJobsAsync(Key("a"), ct));
        Assert.Equal(0, await CountLedgerJobsAsync(Key("b"), ct));
    }

    [Fact(DisplayName = "A long lane lands in the ledger in staging order")]
    public async Task A_long_lane_lands_in_staging_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var labels = Enumerable.Range(0, 30).Select(i => $"n{i:D2}").ToList();
        await Fixture.StageInOneTransactionAsync(SourceTable, [.. labels.Select(l => Row("long", l))]);

        // A tick advances a lane one row per claim, twenty claims at most.
        for (var tick = 0; tick < 3 && await Fixture.CountOutboxAsync(SourceTable) > 0; tick++)
        {
            await Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        }

        var keys = labels.Select(Key).ToList();
        var jobs = await Db.From<Job>().Where(j => j.NamespaceId == NamespaceId).ToListAsync(ct);
        Assert.Equal(keys, jobs.Where(j => keys.Contains(j.DeduplicationKey!)).OrderBy(j => j.Id).Select(j => j.DeduplicationKey));
    }

    private string Key(string label) => $"{label}-{TestId}";

    private JobEnqueueRequest Row(string lane, string label, string jobName = "lane-step") =>
        new(TestNamespace, jobName, JobPayload.Json(new LaneStep(lane, label)), DeduplicationKey: Key(label), Lane: $"{lane}-{TestId}");
}
