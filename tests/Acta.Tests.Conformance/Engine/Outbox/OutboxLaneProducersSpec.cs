using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// Producer transactions open at the same time have no order between them. The relay sees a row only once its
/// transaction commits, so a committed row of a lane relays ahead of an older row whose transaction is still
/// open; each transaction's own rows keep their staging order. SQLite runs one writer at a time, so its
/// producers always stage in sequence and none of these races forms there.
/// </summary>
[ConformanceSpec(
    "outbox.lane-producers",
    "Concurrent producer transactions relay as each commits, in their own order",
    Area = "Outbox",
    Contract = "The relay sees a producer's rows only once it commits: a laned row can pass an older uncommitted one, a rolled-back row never relays, and other lanes flow."
)]
public sealed class OutboxLaneProducersSpec : OutboxRelayIntegrationBase
{
    [Fact(DisplayName = "Rows of a transaction that commits second relay after a younger transaction's rows that committed first")]
    public async Task Reverse_commit_order_relays_each_transaction_in_its_own_staging_order()
    {
        SkipOnSqlite();
        var ct = TestContext.Current.CancellationToken;
        await using var older = await Fixture.BeginOutboxProducerAsync(SourceTable);
        await older.StageAsync(Row("shared", "a1"));
        await older.StageAsync(Row("shared", "a2"));
        await using (var younger = await Fixture.BeginOutboxProducerAsync(SourceTable))
        {
            await younger.StageAsync(Row("shared", "b1"));
            await younger.StageAsync(Row("shared", "b2"));
            await younger.CommitAsync();
        }

        await TickAsync(ct);
        Assert.Equal(Keys("b1", "b2"), await RelayedAsync(ct));

        await older.CommitAsync();
        await TickAsync(ct);
        Assert.Equal(Keys("b1", "b2", "a1", "a2"), await RelayedAsync(ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A rolled-back producer's row never relays and holds nothing behind it")]
    public async Task Rolled_back_row_never_relays()
    {
        SkipOnSqlite();
        var ct = TestContext.Current.CancellationToken;
        await using var older = await Fixture.BeginOutboxProducerAsync(SourceTable);
        await older.StageAsync(Row("shared", "a1"));
        await Fixture.StageInOneTransactionAsync(SourceTable, [Row("shared", "b1")]);

        await TickAsync(ct);
        await older.RollbackAsync();
        await Fixture.StageInOneTransactionAsync(SourceTable, [Row("shared", "b2")]);
        await TickAsync(ct);

        Assert.Equal(Keys("b1", "b2"), await RelayedAsync(ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "An open producer on one lane holds no other lane")]
    public async Task Open_producer_holds_no_other_lane()
    {
        SkipOnSqlite();
        var ct = TestContext.Current.CancellationToken;
        await using var open = await Fixture.BeginOutboxProducerAsync(SourceTable);
        await open.StageAsync(Row("held", "a1"));
        await Fixture.StageInOneTransactionAsync(SourceTable, [Row("free", "b1"), Row("free", "b2")]);

        await TickAsync(ct);
        Assert.Equal(Keys("b1", "b2"), await RelayedAsync(ct));

        await open.CommitAsync();
        await TickAsync(ct);
        Assert.Equal(Keys("b1", "b2", "a1"), await RelayedAsync(ct));
    }

    private void SkipOnSqlite() =>
        Assert.SkipWhen(
            Fixture.DialectToken == "sqlite",
            "SQLite runs one writer at a time, so a second producer transaction cannot open beside the first."
        );

    private Task TickAsync(CancellationToken ct) => Relay(SourceStore, OwnedSubmission).RunTickAsync(TickOptions(), ct);

    // This test's relayed jobs in ledger order, which is the order the relay enqueued them.
    private async Task<List<string>> RelayedAsync(CancellationToken ct)
    {
        var jobs = await Db.From<JobEntity>().Where(j => j.NamespaceId == NamespaceId).ToListAsync(ct);
        var ours = jobs.Where(j => j.DeduplicationKey?.EndsWith(TestId, StringComparison.Ordinal) == true);
        return [.. ours.OrderBy(j => j.Id).Select(j => j.DeduplicationKey!)];
    }

    private List<string> Keys(params string[] labels) => [.. labels.Select(Key)];

    private string Key(string label) => $"{label}-{TestId}";

    private EnqueueJobRequest Row(string lane, string label) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), DeduplicationKey: Key(label), Lane: $"{lane}-{TestId}");
}
