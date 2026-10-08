using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the relay source's own keyset read of its Quarantined rows (<c>ListQuarantinedOutbox</c>), the read
/// beneath the operator listing that <c>ListOutboxQuarantinedSpec</c> specifies.
/// </summary>
[ConformanceSpec(
    "list-quarantined-outbox.keyset-page",
    "The source pages its Quarantined rows by outbox id with their failure evidence",
    Area = "Outbox",
    Contract = "ListQuarantinedOutbox returns up to a page of Quarantined rows after the cursor id in the source's outbox_id order, with identity and failure evidence."
)]
public sealed class ListQuarantinedOutboxSpec : OutboxSpecBase
{
    [Fact(DisplayName = "A source with no Quarantined rows reads an empty page")]
    public async Task No_quarantined_rows_read_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAndClaimAsync(TestKey("claimed"), ct);
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("pending")));

        Assert.Empty(await ListAsync(pageSize: 10, after: null, ct));
    }

    [Fact(DisplayName = "Each row carries its identity, routing keys, staging instant, and failure evidence, and other statuses stay out")]
    public async Task Rows_carry_identity_and_failure_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var stagedAt = new DateTime(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var seeded = DueRow(TestKey("seeded"), status: (byte)OutboxStatusCode.Quarantined, failureCount: 4) with
        {
            CreatedAtUtc = stagedAt,
        };
        await Fixture.SeedOutboxRowAsync(TableName, seeded);
        var relayed = await SeedAndQuarantineAsync(TestKey("relayed"), "poison payload", ct);
        await Fixture.SeedOutboxRowAsync(TableName, DueRow(TestKey("pending")));

        var page = await ListAsync(pageSize: 10, after: null, ct);

        Assert.Equal(2, page.Count);
        var fromSeed = Assert.Single(page, r => r.OutboxId == seeded.OutboxId);
        Assert.Equal("orders", fromSeed.JobNamespace);
        Assert.Equal("send", fromSeed.JobName);
        Assert.Equal(seeded.DeduplicationKey, fromSeed.DeduplicationKey);
        Assert.Null(fromSeed.CorrelationKey);
        Assert.Null(fromSeed.TenantKey);
        Assert.Equal(4, fromSeed.FailureCount);
        Assert.Null(fromSeed.LastError);
        Assert.Equal(stagedAt, fromSeed.CreatedAtUtc);

        var fromRelay = Assert.Single(page, r => r.OutboxId == relayed);
        Assert.Equal(5, fromRelay.FailureCount);
        Assert.Equal("poison payload", fromRelay.LastError);
    }

    [Fact(DisplayName = "Keyset pages walk every Quarantined row exactly once, in the order one full read returns them")]
    public async Task Pages_walk_every_row_once_in_source_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedQuarantinedAsync(5);
        var full = await ListAsync(pageSize: 100, after: null, ct);
        Assert.Equal(seeded, full.Select(r => r.OutboxId).ToHashSet());

        // The order itself is the provider's uuid order (SQL Server sorts by trailing bytes, SQLite by
        // case-folded text), so the walk is held to the full read rather than to a .NET Guid sort.
        var walked = new List<OutboxQuarantinedListItem>();
        var pageSizes = new List<int>();
        Guid? after = null;
        while (await ListAsync(pageSize: 2, after, ct) is { Count: > 0 } page)
        {
            pageSizes.Add(page.Count);
            walked.AddRange(page);
            after = page[^1].OutboxId;
        }

        Assert.Equal([2, 2, 1], pageSizes);
        Assert.Equal(full.Select(r => r.OutboxId), walked.Select(r => r.OutboxId));
    }

    [Fact(
        DisplayName = "A row that leaves quarantine between pages drops out without shifting the rest, and the last row's cursor reads empty"
    )]
    public async Task Row_leaving_between_pages_drops_out()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedQuarantinedAsync(4);
        var full = (await ListAsync(pageSize: 100, after: null, ct)).Select(r => r.OutboxId).ToList();

        var first = await ListAsync(pageSize: 2, after: null, ct);
        Assert.Equal(full.Take(2), first.Select(r => r.OutboxId));

        Assert.Equal([full[2]], (await Store.RequeueQuarantinedAsync(new RequeueQuarantinedOutboxCommand([full[2]]), ct)).OutboxIds);

        Assert.Equal([full[3]], (await ListAsync(pageSize: 2, after: first[^1].OutboxId, ct)).Select(r => r.OutboxId));
        Assert.Empty(await ListAsync(pageSize: 2, after: full[3], ct));
    }

    private Task<IReadOnlyList<OutboxQuarantinedListItem>> ListAsync(int pageSize, Guid? after, CancellationToken ct) =>
        Store.ListQuarantinedAsync(new ListQuarantinedOutboxQuery(pageSize, after), ct);

    private async Task<HashSet<Guid>> SeedQuarantinedAsync(int count)
    {
        var ids = new HashSet<Guid>();
        for (var i = 0; i < count; i++)
        {
            var row = DueRow(TestKey($"stuck-{i}"), status: (byte)OutboxStatusCode.Quarantined, failureCount: 5);
            await Fixture.SeedOutboxRowAsync(TableName, row);
            ids.Add(row.OutboxId);
        }

        return ids;
    }
}
