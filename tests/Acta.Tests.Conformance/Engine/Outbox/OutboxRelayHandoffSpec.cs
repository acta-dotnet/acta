using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// The relay handoff boundary between an external source and the Acta ledger: the crash windows either
/// side of the target enqueue never lose a row and never duplicate a target job. Drives the real
/// <see cref="OutboxRelay"/> composition with a failure-injecting store/target seam.
/// </summary>
[ConformanceSpec(
    "outbox.relay-handoff",
    "Relay crash windows never lose a row or duplicate a target job",
    Area = "Outbox",
    Contract = "A relay tick that fails before target enqueue reclaims the row, and one that fails after enqueue still yields exactly one target job."
)]
public sealed class OutboxRelayHandoffSpec : OutboxRelayIntegrationBase
{
    [Fact(DisplayName = "A failure before target enqueue releases the claim and a later tick delivers the row exactly once")]
    public async Task Failure_before_target_enqueue_reclaims_and_delivers_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var dedup = Unique("hb1");
        var seed = EchoRow(dedup);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);

        var store = new HookedOutboxStore(SourceConnection);
        var target = new HookedJobSubmission(Services) { FailInstead = () => new InvalidOperationException("target unavailable") };

        // Tick 1: claim commits, then the target enqueue fails -> infrastructure failure that releases the
        // claim and fails the tick. No target job exists and the source row is back to Pending.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Relay(store.Source, target.Jobs).RunTickAsync(TickOptions(), ct));
        Assert.Equal(0, await CountLedgerJobsAsync(dedup, ct));
        var released = await Fixture.ReadOutboxRowAsync(SourceTable, seed.OutboxId);
        Assert.True(released.Exists);
        Assert.Equal((byte)OutboxStatusCode.Pending, released.StatusCode);

        // Tick 2: the target is healthy -> the reclaimed row is delivered once and deleted.
        target.FailInstead = null;
        await Relay(store.Source, target.Jobs).RunTickAsync(TickOptions(), ct);
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
        Assert.Equal(1, await CountLedgerJobsAsync(dedup, ct));
    }

    [Fact(DisplayName = "A finalize failure after the target commit still leaves exactly one target job and cleans the source on retry")]
    public async Task Finalize_failure_after_target_commit_dedupes_to_one_job_and_cleans_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var dedup = Unique("hb2");
        var seed = EchoRow(dedup);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);

        var failFinalize = true;
        var store = new HookedOutboxStore(SourceConnection)
        {
            BeforeDelete = () => failFinalize ? throw new InvalidOperationException("source finalize crash") : Task.CompletedTask,
        };
        // A short claim lease so the abandoned claim becomes reclaimable on the retry tick without waiting long.
        var opts = TickOptions(leaseTtlSeconds: 1);

        // Tick 1: enqueue commits (one target job), then the source delete crashes -> the tick fails and the
        // row stays Claimed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Relay(store.Source, OwnedSubmission).RunTickAsync(opts, ct));
        Assert.Equal(1, await CountLedgerJobsAsync(dedup, ct));
        Assert.Equal(1, await Fixture.CountOutboxAsync(SourceTable));

        // Let the 1s claim lease expire so the retry tick recovers the row.
        await Task.Delay(2500, ct);

        // Tick 2: the reclaimed row re-enqueues and the ledger deduplicates it (already ingested), so the
        // finalize deletes the source row and there is still exactly one target job.
        failFinalize = false;
        await Relay(store.Source, OwnedSubmission).RunTickAsync(opts, ct);
        Assert.Equal(1, await CountLedgerJobsAsync(dedup, ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }

    [Fact(DisplayName = "A retry after the source row is deleted never recreates it")]
    public async Task Retry_after_source_deletion_does_not_recreate_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var dedup = Unique("hb3");
        var seed = EchoRow(dedup);
        await Fixture.SeedOutboxRowAsync(SourceTable, seed);

        var store = new HookedOutboxStore(SourceConnection);
        await Relay(store.Source, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        Assert.Equal(1, await CountLedgerJobsAsync(dedup, ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));

        // A subsequent tick (the post-deletion crash-restart window) claims nothing and resurrects nothing.
        await Relay(store.Source, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
        Assert.Equal(1, await CountLedgerJobsAsync(dedup, ct));
    }

    [Fact(DisplayName = "Duplicate source rows for one key coalesce to a single target job and all delete")]
    public async Task Duplicate_source_rows_coalesce_to_one_job_and_all_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var dedup = Unique("hb4");
        for (var i = 0; i < 3; i++)
        {
            await Fixture.SeedOutboxRowAsync(SourceTable, EchoRow(dedup));
        }
        Assert.Equal(3, await Fixture.CountOutboxAsync(SourceTable));

        await Relay(new HookedOutboxStore(SourceConnection).Source, OwnedSubmission).RunTickAsync(TickOptions(), ct);
        Assert.Equal(1, await CountLedgerJobsAsync(dedup, ct));
        Assert.Equal(0, await Fixture.CountOutboxAsync(SourceTable));
    }
}
