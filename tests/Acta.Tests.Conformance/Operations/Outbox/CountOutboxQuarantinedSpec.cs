using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the source's quarantine total, which the tick summary carries to peers that cannot reach the producer database.</summary>
[ConformanceSpec(
    "count-outbox-quarantined.total",
    "The quarantine count totals the source's Quarantined rows as they come and go",
    Area = "Outbox",
    Contract = "CountOutboxQuarantined returns the number of Quarantined rows in the source, zero when there are none, excluding Pending and Claimed rows."
)]
public sealed class CountOutboxQuarantinedSpec : OutboxSpecBase
{
    [Fact(DisplayName = "An empty source counts zero")]
    public async Task Empty_source_counts_zero()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(0L, await Store.CountQuarantinedAsync(ct));
    }

    [Fact(DisplayName = "Only Quarantined rows count, not due or backed-off Pending rows or Claimed ones")]
    public async Task Only_quarantined_rows_count()
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("due")));
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("later"), minutesAgo: -5));
        await Fixture.SeedOutboxRowAsync(
            TableName,
            DueRow(TestKey("claimed")) with
            {
                StatusCode = (byte)OutboxStatusCode.Claimed,
                ClaimToken = Guid.NewGuid(),
                ClaimUntilUtc = DateTime.UtcNow.AddMinutes(5),
            }
        );
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("stuck-a"), status: (byte)OutboxStatusCode.Quarantined));
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("stuck-b"), status: (byte)OutboxStatusCode.Quarantined));

        Assert.Equal(2L, await Store.CountQuarantinedAsync(ct));
    }

    [Fact(DisplayName = "The count rises as rows quarantine and falls as an operator requeues or discards them")]
    public async Task Count_follows_the_quarantine()
    {
        var ct = TestContext.Current.CancellationToken;
        var requeued = await SeedAndQuarantineAsync(TestKey("requeue"), "poison", ct);
        var discarded = await SeedAndQuarantineAsync(TestKey("discard"), "poison", ct);
        Assert.Equal(2L, await Store.CountQuarantinedAsync(ct));

        Assert.Equal([requeued], (await Store.RequeueQuarantinedAsync(new RequeueQuarantinedOutboxCommand([requeued]), ct)).OutboxIds);
        Assert.Equal(1L, await Store.CountQuarantinedAsync(ct));

        Assert.Equal([discarded], (await Store.DiscardQuarantinedAsync(new DiscardQuarantinedOutboxCommand([discarded]), ct)).OutboxIds);
        Assert.Equal(0L, await Store.CountQuarantinedAsync(ct));
    }
}
