using Acta.Features.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// The relay's quarantine boundary: a persistent recoverable rejection quarantines at the threshold with
/// one summarized system-job failure, and a deterministically malformed or oversized row quarantines
/// immediately without accruing retries or touching the target.
/// </summary>
[ConformanceSpec(
    "outbox.relay-quarantine",
    "Threshold and contract failures quarantine with one bounded summary",
    Area = "Outbox",
    Contract = "The fifth persistent recoverable rejection quarantines a row, and malformed or oversized rows quarantine immediately.",
    Arrange = "A live source table holds a row toward an unregistered route, or rows carrying malformed meta, an oversized payload, or an unsupported format id.",
    Act = "The relay ticks five times against the unresolved route, or once with rows the reconstruction rejects before the target.",
    Assert = "Every offending row is quarantined and the tick raises exactly one summarized failure covering all of them."
)]
[CoversStoreMethod(typeof(IOutboxRelayStore), nameof(IOutboxRelayStore.QuarantineAsync))]
public abstract class OutboxRelayQuarantineSpec<TFixture> : OutboxRelayIntegrationBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "The fifth routing rejection quarantines the row and raises one summarized failure, earlier ticks staying quiet")]
    public async Task Threshold_routing_rejection_quarantines_on_the_fifth_tick()
    {
        var ct = TestContext.Current.CancellationToken;
        // A row toward a route that never resolves: the ledger rejects it as a recoverable routing
        // rejection on every tick, so the row accrues one real failure per tick.
        var unknownNs = TestKey("q7-unknown");
        var seed = EchoRow(TestKey("q7"), jobNamespace: unknownNs);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);

        var store = new HookedOutboxStore(SourceStore);

        // Ticks 1..4: below the default threshold of five, each rejection reschedules quietly (no throw).
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await Relay(store, OwnedTarget).RunTickAsync(TickOptions(), ct);
            var pending = await Fixture.ReadOutboxRowAsync(SourceTable, seed.OutboxId);
            Assert.Equal((byte)OutboxStatusCode.Pending, pending.StatusCode);
            Assert.Equal(attempt, pending.FailureCount);
            await Fixture.RewindOutboxAsync(SourceTable); // make it due for the next tick
        }

        // Tick 5: the fifth rejection reaches the threshold, quarantines the row, and raises one bounded summary.
        var summary = await Assert.ThrowsAsync<OutboxQuarantineTickException>(() =>
            Relay(store, OwnedTarget).RunTickAsync(TickOptions(), ct)
        );
        Assert.Equal(1, summary.QuarantinedCount);

        var row = await Fixture.ReadOutboxRowAsync(SourceTable, seed.OutboxId);
        Assert.True(row.Exists);
        Assert.Equal((byte)OutboxStatusCode.Quarantined, row.StatusCode);
        Assert.Equal(5, row.FailureCount);
    }

    [Fact(
        DisplayName = "Malformed meta, an oversized payload, and an unsupported format id quarantine immediately without touching the target"
    )]
    public async Task Malformed_and_oversize_rows_quarantine_immediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var malformed = EchoRow(TestKey("q8a"), meta: """{"tags":[{"value":"no-name"}]}""");
        var oversize = EchoRow(TestKey("q8b"), data: new byte[128]);
        // A valid-byte but reserved (unsupported) input_format_id: the DDL's 0-255 guard admits it, but the
        // relay's reconstruction rejects it as a contract error, so it quarantines rather than failing the tick.
        var unsupportedFormat = EchoRow(TestKey("q8c"), inputFormatId: 4);
        await Fixture.SeedOutboxRowAsync(SourceTable, malformed);
        await Fixture.SeedOutboxRowAsync(SourceTable, oversize);
        await Fixture.SeedOutboxRowAsync(SourceTable, unsupportedFormat);

        var store = new HookedOutboxStore(SourceStore);
        // The target must never be reached for a contract-rejected row; fail loudly if it is.
        var target = new HookedOutboxTarget(OwnedTarget) { FailInstead = () => new InvalidOperationException("target must not be called") };

        // A tiny payload cap so the oversized row is over the target inline limit; the echo payload stays under it.
        var summary = await Assert.ThrowsAsync<OutboxQuarantineTickException>(() =>
            Relay(store, target).RunTickAsync(TickOptions(maxPayload: 64), ct)
        );
        Assert.Equal(3, summary.QuarantinedCount);

        foreach (var seed in new[] { malformed, oversize, unsupportedFormat })
        {
            var row = await Fixture.ReadOutboxRowAsync(SourceTable, seed.OutboxId);
            Assert.True(row.Exists);
            Assert.Equal((byte)OutboxStatusCode.Quarantined, row.StatusCode);
            Assert.Equal(0, row.FailureCount); // immediate quarantine, no retry churn
        }
    }
}
