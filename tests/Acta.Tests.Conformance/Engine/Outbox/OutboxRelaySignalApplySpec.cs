using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Outbox;

/// <summary>
/// End-to-end conformance for the outbox operator path: a parked requeue signal is applied by the
/// relay pass against the real source and ledger - the quarantined row returns to Pending, the
/// evidence event lands on the slot job, the signal row is consumed, and the same pass's drain then
/// relays the freed row into a ledger job. This is the accepted-then-applied loop the plan promises.
/// </summary>
[ConformanceSpec(
    "outbox-signal.applied-by-tick",
    "A parked requeue is applied, evidenced, consumed, and the freed row relays",
    Area = "Outbox",
    Contract = "The relay pass applies a parked requeue before draining: the row returns to Pending, the evidence event lands, the signal is consumed, and the freed row relays."
)]
public sealed class OutboxRelaySignalApplySpec : OutboxRelayIntegrationBase
{
    [Fact(DisplayName = "Requeue applies before the drain: row freed, evidence written, signal consumed, job relayed")]
    public async Task Parked_requeue_frees_the_row_and_the_same_pass_relays_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var signals = Services.GetRequiredService<OutboxOperations>();
        var dedup = Unique("sig-apply");

        var row = EchoRow(dedup, status: 90, failureCount: 5);
        await Fixture.SeedOutboxRowAsync(SourceTable, row);

        var slotJobId = await SeedSlotJobAsync(ct);
        Assert.Equal(1, await ParkAsync(slotJobId, OutboxSignalNames.Requeue, "fixed the target route", [row.OutboxId], ct));

        var relay = Relay(SourceStore, OwnedSubmission);
        var (requeued, discarded) = await relay.ApplyOperatorSignalsAsync(slotJobId, signals, ct);
        Assert.Equal(1, requeued);
        Assert.Equal(0, discarded);

        // The signal row is consumed, the source row is Pending with its budget reset, and the
        // evidence event carries the operator's actor, justification, and the applied id.
        Assert.Null(await signals.GetOutboxSignalAsync(slotJobId, OutboxSignalNames.Requeue, ct));
        var state = await Fixture.ReadOutboxRowAsync(SourceTable, row.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Pending, state.StatusCode);
        Assert.Equal(0, state.FailureCount);

        var evidence = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Job && e.EntityId == slotJobId).ToListAsync(ct)
        );
        Assert.Equal(EventCode.OutboxRequeued, evidence.EventCode);
        Assert.Equal("marko", EventDetail(evidence, "actor"));
        Assert.Contains("fixed the target route", evidence.EventMessage);
        Assert.Contains(row.OutboxId.ToString(), evidence.EventMessage);

        // The same pass's drain now claims the freed row and relays it into a ledger job.
        var summary = await relay.RunTickAsync(TickOptions(), ct);
        Assert.Equal(1, summary.Relayed);
        Assert.Equal(1, await CountLedgerJobsAsync(dedup, ct));
    }
}
