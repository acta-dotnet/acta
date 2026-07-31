using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the external-outbox claim: one short source transaction claims a bounded, priority-
/// ordered batch of due Pending rows, stamps one token and a database-clock lease, and never hands the
/// same row to two claims. Future rows stay unclaimed.
/// </summary>
[ConformanceSpec(
    "outbox-claim.priority-bounded",
    "Claim takes a bounded urgent-first batch under one token, no double claim",
    Area = "Outbox",
    Contract = "ClaimDue claims a bounded urgent-first batch of due Pending rows, stamps one token and a database-clock lease, and claims no row twice.",
    Arrange = "A source outbox table holds several due Pending rows of differing priority plus a future row.",
    Act = "ClaimDue runs with a batch smaller than the due set, then again with a fresh token.",
    Assert = "The urgent rows are claimed first, each claimed row is disjoint and leased, and the future row stays Pending."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.ClaimDueAsync))]
public abstract class OutboxClaimSpec<TFixture> : OutboxSpecBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Claim prefers higher priority and leaves the rest Pending")]
    public async Task Claim_prefers_higher_priority()
    {
        var ct = TestContext.Current.CancellationToken;
        var low = DueRow(TestKey("low"), priority: 0);
        var normal = DueRow(TestKey("normal"), priority: 50);
        var high = DueRow(TestKey("high"), priority: 100);
        await Fixture.SeedOutboxRowAsync(TableName, low);
        await Fixture.SeedOutboxRowAsync(TableName, normal);
        await Fixture.SeedOutboxRowAsync(TableName, high);

        var before = DateTime.UtcNow;
        var claimed = await ClaimAsync(Guid.NewGuid(), batchSize: 1, ct);

        var one = Assert.Single(claimed);
        Assert.Equal(high.OutboxId, one.OutboxId);

        var claimedState = await Fixture.ReadOutboxRowAsync(TableName, high.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Claimed, claimedState.StatusCode);
        Assert.NotNull(claimedState.ClaimToken);
        Assert.True(claimedState.ClaimUntilUtc > before, "lease is stamped from the database clock into the future");

        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(TableName, normal.OutboxId)).StatusCode);
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(TableName, low.OutboxId)).StatusCode);
    }

    [Fact(DisplayName = "At equal priority the older row claims first")]
    public async Task Claim_is_fifo_at_equal_priority()
    {
        var ct = TestContext.Current.CancellationToken;
        var older = DueRow(TestKey("older"), priority: 50, minutesAgo: 10);
        var newer = DueRow(TestKey("newer"), priority: 50, minutesAgo: 2);
        // Seed newest first so a claim that respected insertion order rather than age would pick wrong.
        await Fixture.SeedOutboxRowAsync(TableName, newer);
        await Fixture.SeedOutboxRowAsync(TableName, older);

        var claimed = await ClaimAsync(Guid.NewGuid(), batchSize: 1, ct);

        var one = Assert.Single(claimed);
        Assert.Equal(older.OutboxId, one.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(TableName, newer.OutboxId)).StatusCode);
    }

    [Fact(DisplayName = "Two claims split the backlog disjointly and never double claim a row")]
    public async Task Two_claims_never_overlap()
    {
        var ct = TestContext.Current.CancellationToken;
        var rows = new[] { DueRow(TestKey("a")), DueRow(TestKey("b")), DueRow(TestKey("c")) };
        foreach (var row in rows)
        {
            await Fixture.SeedOutboxRowAsync(TableName, row);
        }

        var first = await ClaimAsync(Guid.NewGuid(), batchSize: 2, ct);
        var second = await ClaimAsync(Guid.NewGuid(), batchSize: 2, ct);
        var third = await ClaimAsync(Guid.NewGuid(), batchSize: 2, ct);

        Assert.Equal(2, first.Count);
        Assert.Single(second);
        Assert.Empty(third);

        var claimedIds = first.Concat(second).Select(r => r.OutboxId).ToList();
        Assert.Equal(3, claimedIds.Distinct().Count());
        Assert.Equal(rows.Select(r => r.OutboxId).OrderBy(x => x), claimedIds.OrderBy(x => x));
    }

    [Fact(DisplayName = "Two simultaneous claimers split the backlog with no overlap")]
    public async Task Concurrent_claimers_never_double_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        const int total = 40;
        var seeded = new List<Guid>(total);
        for (var i = 0; i < total; i++)
        {
            var row = DueRow(TestKey($"c{i}"));
            seeded.Add(row.OutboxId);
            await Fixture.SeedOutboxRowAsync(TableName, row);
        }

        // Two genuinely simultaneous claimers over two store instances, exercising SKIP LOCKED / READPAST
        // / BEGIN IMMEDIATE. The safety invariant is that no row is handed to both at once; how the batch
        // splits between them is provider timing (mssql page locking on a small table may give one 0).
        var second = (IOutboxRelayStore)Fixture.CreateOutboxStore(TableName);
        var claimA = Store.ClaimDueAsync(new ClaimOutboxCommand(Guid.NewGuid(), total / 2, LeaseTtlSeconds), ct);
        var claimB = second.ClaimDueAsync(new ClaimOutboxCommand(Guid.NewGuid(), total / 2, LeaseTtlSeconds), ct);
        var results = await Task.WhenAll(claimA, claimB);

        var concurrent = results[0].Concat(results[1]).Select(r => r.OutboxId).ToList();
        Assert.Equal(concurrent.Count, concurrent.Distinct().Count());

        // Draining the remainder proves every seeded row is claimable exactly once overall: no row was
        // lost to a double claim and none was stranded.
        var rest = await ClaimAsync(Guid.NewGuid(), total, ct);
        var all = concurrent.Concat(rest.Select(r => r.OutboxId)).ToList();
        Assert.Equal(total, all.Count);
        Assert.Equal(total, all.Distinct().Count());
        Assert.Equal(seeded.OrderBy(x => x), all.OrderBy(x => x));
    }

    [Fact(DisplayName = "A row whose next attempt is in the future is not claimed")]
    public async Task Future_rows_are_not_claimed()
    {
        var ct = TestContext.Current.CancellationToken;
        var due = DueRow(TestKey("due"));
        var future = DueRow(TestKey("future")) with { NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(30) };
        await Fixture.SeedOutboxRowAsync(TableName, due);
        await Fixture.SeedOutboxRowAsync(TableName, future);

        var claimed = await ClaimAsync(Guid.NewGuid(), batchSize: 10, ct);

        var one = Assert.Single(claimed);
        Assert.Equal(due.OutboxId, one.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(TableName, future.OutboxId)).StatusCode);
    }
}
