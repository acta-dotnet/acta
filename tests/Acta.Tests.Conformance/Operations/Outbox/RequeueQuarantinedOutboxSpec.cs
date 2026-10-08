using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>
/// Conformance for the operator requeue - the exit <c>OutboxStatusCode.Quarantined</c> promises: the row
/// returns to Pending, immediately due, with its failure budget reset and <c>last_error</c> kept as the
/// evidence of why it was quarantined. An id-scoped requeue touches only its ids; a null id set sweeps
/// every quarantined row; a non-quarantined id is untouched (the status filter is the whole guard).
/// </summary>
[ConformanceSpec(
    "outbox-requeue.operator-exit",
    "Requeue returns quarantined rows to Pending, budget reset, evidence kept",
    Area = "Outbox",
    Contract = "RequeueQuarantined moves targeted (or all, when ids are null) Quarantined rows to Pending, due now, failure_count reset, last_error kept, returning the ids."
)]
public sealed class RequeueQuarantinedOutboxSpec : OutboxSpecBase
{
    [Fact(DisplayName = "Id-scoped requeue frees one row, the null form sweeps the rest, and freed rows claim again")]
    public async Task Requeue_resets_budget_keeps_evidence_and_makes_rows_claimable()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await SeedAndQuarantineAsync(TestKey("rq-a"), "poison a", ct);
        var second = await SeedAndQuarantineAsync(TestKey("rq-b"), "poison b", ct);

        var targeted = (await Store.RequeueQuarantinedAsync(new RequeueQuarantinedOutboxCommand([first]), ct)).OutboxIds;
        Assert.Equal([first], targeted);

        var state = await Fixture.ReadOutboxRowAsync(TableName, first);
        Assert.Equal((byte)OutboxStatusCode.Pending, state.StatusCode);
        Assert.Equal(0, state.FailureCount);
        Assert.Equal("poison a", state.LastError);
        Assert.Equal((byte)OutboxStatusCode.Quarantined, (await Fixture.ReadOutboxRowAsync(TableName, second)).StatusCode);

        var swept = (await Store.RequeueQuarantinedAsync(new RequeueQuarantinedOutboxCommand(null), ct)).OutboxIds;
        Assert.Equal([second], swept);
        Assert.Empty((await Store.RequeueQuarantinedAsync(new RequeueQuarantinedOutboxCommand(null), ct)).OutboxIds);

        // Requeued rows are due now, so a fresh claim finds both again.
        var reclaimed = await ClaimAsync(Guid.NewGuid(), batchSize: 10, ct);
        Assert.Equal(new HashSet<Guid> { first, second }, reclaimed.Select(r => r.OutboxId).ToHashSet());
    }
}
