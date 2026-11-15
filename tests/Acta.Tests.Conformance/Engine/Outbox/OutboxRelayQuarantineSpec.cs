using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// The relay's quarantine boundary: a persistent recoverable rejection quarantines at the threshold, and a
/// deterministically malformed or oversized row quarantines immediately without accruing retries or touching
/// the target. The tick answers the rows it quarantined and succeeds.
/// </summary>
[ConformanceSpec(
    "outbox.relay-quarantine",
    "Threshold and contract failures quarantine in a tick that succeeds",
    Area = "Outbox",
    Contract = "The fifth persistent recoverable rejection quarantines a row, and malformed or oversized rows quarantine immediately."
)]
public sealed class OutboxRelayQuarantineSpec : OutboxRelayIntegrationBase
{
    [Fact(DisplayName = "The fifth routing rejection quarantines the row in a tick that succeeds, earlier ticks rescheduling it")]
    public async Task Threshold_routing_rejection_quarantines_on_the_fifth_tick()
    {
        var ct = TestContext.Current.CancellationToken;
        // A row toward a route that never resolves: the ledger rejects it as a recoverable routing
        // rejection on every tick, so the row accrues one real failure per tick.
        var unknownNs = Unique("q7-unknown");
        var seed = EchoRow(Unique("q7"), jobNamespace: unknownNs);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);

        var store = new HookedOutboxStore(SourceConnection);

        // Ticks 1..4: below the default threshold of five, each rejection reschedules the row.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await Relay(store.Source, OwnedSubmission).RunTickAsync(TickOptions(), ct);
            var pending = await Fixture.ReadOutboxRowAsync(SourceTable, seed.OutboxId);
            Assert.Equal((byte)OutboxStatusCode.Pending, pending.StatusCode);
            Assert.Equal(attempt, pending.FailureCount);
            await Fixture.RewindOutboxAsync(SourceTable); // make it due for the next tick
        }

        // Tick 5: the fifth rejection reaches the threshold and quarantines the row, and the tick still succeeds.
        var (summary, quarantined) = await Relay(store.Source, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        Assert.Equal([seed.OutboxId], quarantined);
        Assert.Equal(1, summary.QuarantineTotal);

        var row = await Fixture.ReadOutboxRowAsync(SourceTable, seed.OutboxId);
        Assert.True(row.Exists);
        Assert.Equal((byte)OutboxStatusCode.Quarantined, row.StatusCode);
        Assert.Equal(5, row.FailureCount);
    }

    [Fact(
        DisplayName = "Malformed meta, an oversized payload, an unsupported format id, json holding a NUL byte, and json that is not one value quarantine immediately without touching the target"
    )]
    public async Task Malformed_and_oversize_rows_quarantine_immediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var malformed = EchoRow(Unique("q8a"), meta: """{"tags":[{"value":"no-name"}]}""");
        var oversize = EchoRow(Unique("q8b"), data: new byte[128]);
        // A valid-byte but reserved (unsupported) input_format_id: the DDL's 0-255 guard admits it, but the
        // relay's reconstruction rejects it as a contract error, so it quarantines rather than failing the tick.
        var unsupportedFormat = EchoRow(Unique("q8c"), inputFormatId: 4);
        var jsonWithNul = EchoRow(Unique("q8d"), data: "\"a\0b\""u8.ToArray(), inputFormatId: 1);
        var notJson = EchoRow(Unique("q8e"), data: "{broken"u8.ToArray(), inputFormatId: 1);
        await Fixture.SeedOutboxRowAsync(SourceTable, malformed);
        await Fixture.SeedOutboxRowAsync(SourceTable, oversize);
        await Fixture.SeedOutboxRowAsync(SourceTable, unsupportedFormat);
        await Fixture.SeedOutboxRowAsync(SourceTable, jsonWithNul);
        await Fixture.SeedOutboxRowAsync(SourceTable, notJson);

        var store = new HookedOutboxStore(SourceConnection);
        // The target must never be reached for a contract-rejected row; fail loudly if it is.
        var target = new HookedJobSubmission(Services) { FailInstead = () => new InvalidOperationException("target must not be called") };

        // A tiny payload cap so the oversized row is over the target inline limit; the echo payload stays under it.
        var (_, quarantined) = await Relay(store.Source, target.Jobs).RunTickAsync(TickOptions(maxPayload: 64), ct);
        Assert.Equal(5, quarantined.Count);

        foreach (var seed in new[] { malformed, oversize, unsupportedFormat, jsonWithNul, notJson })
        {
            var row = await Fixture.ReadOutboxRowAsync(SourceTable, seed.OutboxId);
            Assert.True(row.Exists);
            Assert.Equal((byte)OutboxStatusCode.Quarantined, row.StatusCode);
            Assert.Equal(0, row.FailureCount); // immediate quarantine, no retry churn
        }

        Assert.Contains(
            "input does not fit input_format_id 1",
            (await Fixture.ReadOutboxRowAsync(SourceTable, jsonWithNul.OutboxId)).LastError,
            StringComparison.Ordinal
        );
    }
}
