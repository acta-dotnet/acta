using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Base for external-outbox source-store specs. Each test gets its own canonical source table (keyed by
/// <see cref="ActaTestBase{TFixture}.TestId"/>) so parallel specs never claim each other's rows, and a
/// store built over that table. The store is exercised directly, with no Acta-ledger session or DI: a
/// worker relays a source database independently of its own ledger provider.
/// </summary>
public abstract class OutboxSpecBase<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private protected string TableName { get; private set; } = null!;

    private protected IOutboxRelayStore Store { get; private set; } = null!;

    protected const int LeaseTtlSeconds = 180;

    protected override async ValueTask AfterInitializeAsync()
    {
        TableName = "acta_outbox_" + TestId;
        await Fixture.ApplyOutboxDdlAsync(TableName);
        Store = (IOutboxRelayStore)Fixture.CreateOutboxStore(TableName);
    }

    // A due Pending row staged "in the past" so the claim predicate (next_attempt_at_utc <= db_now) is
    // satisfied without waiting; created_at back-dated so priority/FIFO ordering is deterministic.
    private protected static OutboxSeed DueRow(
        string dedup,
        byte? priority = null,
        int minutesAgo = 5,
        byte status = 10,
        int failureCount = 0,
        byte inputFormatId = 0
    ) =>
        new(
            OutboxId: Guid.NewGuid(),
            JobNamespace: "orders",
            JobName: "send",
            InputFormatId: inputFormatId,
            Input: null,
            DeduplicationKey: dedup,
            PriorityCode: priority,
            CreatedAtUtc: DateTime.UtcNow.AddMinutes(-minutesAgo),
            NextAttemptAtUtc: DateTime.UtcNow.AddMinutes(-minutesAgo),
            StatusCode: status,
            FailureCount: failureCount
        );

    // A row already Claimed by a prior relay whose lease has expired, so a fresh claim must recover it.
    private protected OutboxSeed ClaimedExpiredRow(string dedup, Guid staleToken) =>
        OutboxSpecBase<TFixture>.DueRow(dedup) with
        {
            StatusCode = (byte)OutboxStatusCode.Claimed,
            ClaimToken = staleToken,
            ClaimUntilUtc = DateTime.UtcNow.AddMinutes(-1),
        };

    private protected Task<IReadOnlyList<OutboxRow>> ClaimAsync(Guid token, int batchSize, CancellationToken ct) =>
        Store.ClaimDueAsync(new ClaimOutboxCommand(token, batchSize, LeaseTtlSeconds), ct);

    // Seed one due row and claim it, returning its id and the owning token for a finalize proof.
    private protected async Task<(Guid Id, Guid Token)> SeedAndClaimAsync(string dedup, CancellationToken ct)
    {
        var row = OutboxSpecBase<TFixture>.DueRow(dedup);
        await Fixture.SeedOutboxRowAsync(TableName, row);
        var token = Guid.NewGuid();
        var claimed = await ClaimAsync(token, batchSize: 10, ct);
        Assert.Single(claimed);
        return (row.OutboxId, token);
    }
}
