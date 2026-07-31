using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the provider DDL API: with the shape validator gone, the proof that
/// <c>{Provider}OutboxDdl.CreateScript</c> emits a usable table is behavioral. The generated script is
/// applied to the real database, then the real relay store claims, reschedules, quarantines, and deletes
/// against it. This also single-sources the canonical fixture DDL through the same API.
/// </summary>
[ConformanceSpec(
    "outbox-ddl.create-script",
    "Generated outbox DDL yields a working relay source table",
    Area = "Outbox",
    Contract = "The DDL API emits a canonical outbox table the real relay store can claim, reschedule, quarantine, and delete against, proving the shape by behavior.",
    Arrange = "The provider DDL CreateScript output is applied to the test database to create the canonical outbox table.",
    Act = "The relay store claims a seeded row and finalizes seeded rows by delete, reschedule, and quarantine.",
    Assert = "The claimed row deletes, the rescheduled row returns to pending with an incremented failure count, and the quarantined row moves to status ninety."
)]
public abstract class OutboxDdlSpec<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private const int LeaseTtlSeconds = 180;

    [Fact(DisplayName = "The generated canonical table supports claim, delete, reschedule, and quarantine")]
    public Task Canonical_table_round_trips() => RoundTripAsync("acta_outbox");

    protected async Task RoundTripAsync(string table)
    {
        var ct = TestContext.Current.CancellationToken;
        await Fixture.ApplyOutboxDdlAsync(table);
        var store = (IOutboxRelayStore)Fixture.CreateOutboxStore(table);

        // Claim then delete.
        var toDelete = DueSeed();
        await Fixture.SeedOutboxRowAsync(table, toDelete);
        var deleteToken = Guid.NewGuid();
        Assert.Single(await store.ClaimDueAsync(new ClaimOutboxCommand(deleteToken, 10, LeaseTtlSeconds), ct));
        await store.DeleteClaimedAsync(new FinalizeOutboxCommand(deleteToken, [toDelete.OutboxId]), ct);
        Assert.False((await Fixture.ReadOutboxRowAsync(table, toDelete.OutboxId)).Exists);

        // Claim then reschedule back to pending with an incremented failure count.
        var toReschedule = DueSeed();
        await Fixture.SeedOutboxRowAsync(table, toReschedule);
        var rescheduleToken = Guid.NewGuid();
        Assert.Single(await store.ClaimDueAsync(new ClaimOutboxCommand(rescheduleToken, 10, LeaseTtlSeconds), ct));
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
        Assert.Single(await store.ClaimDueAsync(new ClaimOutboxCommand(quarantineToken, 10, LeaseTtlSeconds), ct));
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
            InputData: null,
            DeduplicationKey: Guid.NewGuid().ToString("N"),
            PriorityCode: null,
            CreatedAtUtc: DateTime.UtcNow.AddMinutes(-5),
            NextAttemptAtUtc: DateTime.UtcNow.AddMinutes(-5),
            StatusCode: 10,
            FailureCount: 0
        );
}
