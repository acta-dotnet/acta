using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the provider DDL API: with the shape validator gone, the proof that
/// <c>{Provider}Migrations.CreateOutboxScript</c> emits a usable table is behavioral. The generated script is
/// applied to the real database, then the real relay store claims, reschedules, quarantines, and deletes
/// against it. This also single-sources the canonical fixture DDL through the same API, and proves that
/// <c>ApplyOutboxAsync</c> creates that table once however many callers race it.
/// </summary>
[ConformanceSpec(
    "outbox-ddl.create-script",
    "Generated outbox DDL yields a working relay source table",
    Area = "Outbox",
    Contract = "The DDL API's table serves the real relay store's claim, reschedule, quarantine, and delete, and ApplyOutboxAsync creates it once under racing callers."
)]
public sealed class OutboxDdlSpec : ActaTestBase<ConformanceFixture>
{
    private const int LeaseTtlSeconds = 180;

    [Fact(DisplayName = "The generated canonical table supports claim, delete, reschedule, and quarantine")]
    public Task Canonical_table_round_trips() => RoundTripAsync("acta_outbox");

    [Fact(DisplayName = "The DDL API honors a table override and the store round-trips against it")]
    public Task Table_override_round_trips() => RoundTripAsync("acta_outbox_ddl");

    [Fact(DisplayName = "Eight concurrent installs create the outbox table once, and an install leaves an existing table as it is")]
    public async Task Concurrent_installs_create_the_table_once_and_leave_it_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var table = "acta_outbox_" + TestId;
        try
        {
            // One gate around the whole race: the eight race each other's install lock and no other spec's DDL.
            var created = await TestDdl.RunAsync(() =>
                Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Fixture.ApplyOutboxAsync(table), ct)))
            );
            Assert.Single(created, wasCreated => wasCreated);

            var kept = DueSeed();
            await Fixture.SeedOutboxRowAsync(table, kept);
            Assert.False(await TestDdl.RunAsync(() => Fixture.ApplyOutboxAsync(table)));
            Assert.True((await Fixture.ReadOutboxRowAsync(table, kept.OutboxId)).Exists);
        }
        finally
        {
            await TestDdl.RunAsync(() => Fixture.DropOutboxAsync(table));
        }
    }

    [Fact(DisplayName = "An install with no schema creates the table where a relay source that names no schema reads it")]
    public async Task An_install_with_no_schema_lands_where_an_unqualified_source_reads()
    {
        Assert.SkipWhen(Fixture.DialectToken == "sqlite", "SQLite has one schema, main.");
        var ct = TestContext.Current.CancellationToken;
        var table = "acta_outbox_" + TestId;
        try
        {
            Assert.True(await TestDdl.RunAsync(() => Fixture.ApplyOutboxAsync(table, unqualified: true)));
            Assert.False(await TestDdl.RunAsync(() => Fixture.ApplyOutboxAsync(table, unqualified: true)));

            // An empty claim through a source with no schema runs only if the table is where that source resolves it.
            var source = Fixture.OpenOutboxSource(table, unqualified: true);
            Assert.Empty((await source.ClaimAsync(new ClaimOutboxCommand(Guid.NewGuid(), 10, LeaseTtlSeconds), ct)).Rows);
        }
        finally
        {
            await TestDdl.RunAsync(() => Fixture.DropOutboxAsync(table, unqualified: true));
        }
    }

    private async Task RoundTripAsync(string table)
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.ApplyOutboxDdlAsync(table);
        var store = Fixture.OpenOutboxSource(table);

        // Claim then delete.
        var toDelete = DueSeed();
        await Fixture.SeedOutboxRowAsync(table, toDelete);
        var deleteToken = Guid.NewGuid();
        Assert.Single((await store.ClaimAsync(new ClaimOutboxCommand(deleteToken, 10, LeaseTtlSeconds), ct)).Rows);
        await store.RemoveClaimedAsync(new RemoveClaimedOutboxCommand(deleteToken, [toDelete.OutboxId]), ct);
        Assert.False((await Fixture.ReadOutboxRowAsync(table, toDelete.OutboxId)).Exists);

        // Claim then reschedule back to pending with an incremented failure count.
        var toReschedule = DueSeed();
        await Fixture.SeedOutboxRowAsync(table, toReschedule);
        var rescheduleToken = Guid.NewGuid();
        Assert.Single((await store.ClaimAsync(new ClaimOutboxCommand(rescheduleToken, 10, LeaseTtlSeconds), ct)).Rows);
        await store.RescheduleAsync(
            new RescheduleOutboxCommand(rescheduleToken, [new OutboxReschedule(toReschedule.OutboxId, 1, 60, "recoverable")]),
            ct
        );
        var rescheduled = await Fixture.ReadOutboxRowAsync(table, toReschedule.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Pending, rescheduled.StatusCode);
        Assert.Equal(1, rescheduled.FailureCount);

        // Claim then quarantine.
        var toQuarantine = DueSeed();
        await Fixture.SeedOutboxRowAsync(table, toQuarantine);
        var quarantineToken = Guid.NewGuid();
        Assert.Single((await store.ClaimAsync(new ClaimOutboxCommand(quarantineToken, 10, LeaseTtlSeconds), ct)).Rows);
        await store.QuarantineAsync(
            new QuarantineOutboxCommand(quarantineToken, [new OutboxQuarantine(toQuarantine.OutboxId, 5, "poison")]),
            ct
        );
        Assert.Equal((byte)OutboxStatusCode.Quarantined, (await Fixture.ReadOutboxRowAsync(table, toQuarantine.OutboxId)).StatusCode);
    }

    // A due Pending row staged in the past so the claim predicate is satisfied at once; a none payload keeps
    // the payload-pair check satisfied without binding bytes.
    private static OutboxSeed DueSeed() =>
        new(
            OutboxId: Guid.NewGuid(),
            JobNamespace: "orders",
            JobName: "send",
            InputFormatId: 0,
            Input: null,
            DeduplicationKey: Guid.NewGuid().ToString("N"),
            PriorityCode: null,
            CreatedAtUtc: DateTime.UtcNow.AddMinutes(-5),
            NextAttemptAtUtc: DateTime.UtcNow.AddMinutes(-5),
            StatusCode: 10,
            FailureCount: 0
        );
}
