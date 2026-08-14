using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the operator's quarantine window: the keyset listing pages every Quarantined row by
/// <c>outbox_id</c> with its failure evidence and no payload, and the count reports the current total -
/// the number the tick summary carries cross-peer.
/// </summary>
[ConformanceSpec(
    "outbox-quarantine.listing",
    "Quarantined rows list in keyset pages with their failure evidence",
    Area = "Outbox",
    Contract = "ListQuarantined pages every Quarantined row by outbox_id with identity and failure evidence, and CountQuarantined reports the current total.",
    Arrange = "Three claimed rows are quarantined with distinct errors.",
    Act = "The listing is read in two keyset pages and the quarantine total is counted.",
    Assert = "The two pages cover all three rows exactly once with failure evidence intact, and the count is three."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.ListQuarantinedAsync))]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.CountQuarantinedAsync))]
public abstract class OutboxListQuarantinedSpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Two keyset pages cover every quarantined row exactly once, evidence intact")]
    public async Task Listing_pages_by_outbox_id_and_count_reports_the_total()
    {
        var ct = TestContext.Current.CancellationToken;
        var quarantined = new List<Guid>();
        foreach (var key in new[] { "list-a", "list-b", "list-c" })
        {
            quarantined.Add(await SeedAndQuarantineAsync(TestKey(key), $"error for {key}", ct));
        }

        // A Pending row proves the status filter: it must appear in neither page nor count.
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("list-pending")));

        Assert.Equal(3, await Store.CountQuarantinedAsync(ct));

        var first = await Store.ListQuarantinedAsync(new ListQuarantinedOutboxCommand(PageSize: 2, AfterOutboxId: null), ct);
        Assert.Equal(2, first.Count);
        var second = await Store.ListQuarantinedAsync(new ListQuarantinedOutboxCommand(PageSize: 2, AfterOutboxId: first[^1].OutboxId), ct);
        Assert.Single(second);

        // Exactly-once coverage is the paging contract. The row order itself is the provider's own
        // uuid ordering (SQL Server sorts UNIQUEIDENTIFIER by trailing bytes), so pages are asserted
        // disjoint and complete, not .NET-Guid-sorted.
        var listed = first.Concat(second).ToList();
        Assert.Equal(3, listed.Select(r => r.OutboxId).Distinct().Count());
        Assert.Equal(quarantined.ToHashSet(), listed.Select(r => r.OutboxId).ToHashSet());

        // Identity and failure evidence ride every row; the payload never leaves the producer table.
        Assert.All(listed, row => Assert.Equal("orders", row.JobNamespace));
        Assert.All(listed, row => Assert.Equal(5, row.FailureCount));
        Assert.All(listed, row => Assert.StartsWith("error for ", row.LastError!, StringComparison.Ordinal));
    }
}
