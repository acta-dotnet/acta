using System.Text.Json;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

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
    Contract = "The relay pass applies a parked requeue before draining: the row returns to Pending, the evidence event lands, the signal is consumed, and the freed row relays.",
    Arrange = "A source row sits Quarantined and a requeue command is parked on the slot job's signal inbox.",
    Act = "One operator-signal apply runs, then one relay tick.",
    Assert = "The apply reports one requeued row and consumes the signal, the evidence event carries actor and ids, and the tick relays the freed row into a ledger job."
)]
public abstract class OutboxRelaySignalApplySpec<TFixture> : OutboxRelayIntegrationBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Requeue applies before the drain: row freed, evidence written, signal consumed, job relayed")]
    public async Task Parked_requeue_frees_the_row_and_the_same_pass_relays_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var signals = Services.GetRequiredService<IOutboxSignalStore>();
        var dedup = TestKey("sig-apply");

        var row = EchoRow(dedup, status: 90, failureCount: 5);
        await Fixture.SeedOutboxRowAsync(SourceTable, row);

        // A ledger job stands in for the sys.outbox slot (this harness drives the relay composition
        // directly, like the crash-window specs); the runtimes row backs the evidence event join.
        var seeder = new ActaTestSeeder(Db);
        var (slotJobId, _) = await seeder.SeedJobAsync(NamespaceId, ct: ct);
        await Db.From<JobRuntime>()
            .InsertAsync<long>(
                new JobRuntime
                {
                    Id = slotJobId,
                    NamespaceId = NamespaceId,
                    Status = JobStatusCode.Ready,
                    Priority = JobPriorityCode.Critical,
                    ExecutionNumber = 1,
                },
                ct
            );

        var payload = new OutboxSignalPayload(Guid.NewGuid(), "marko", "fixed the target route", [row.OutboxId]);
        var admitted = await signals.ParkAsync(
            new ParkOutboxSignalCommand(
                slotJobId,
                OutboxSignalNames.Requeue,
                ValueFormatId: 1,
                JsonSerializer.SerializeToUtf8Bytes(payload, OutboxSignalJsonContext.Default.OutboxSignalPayload),
                StaleBeforeUtc: DateTime.UtcNow.AddHours(-1)
            ),
            ct
        );
        Assert.Equal(1, admitted.Action);

        var relay = Relay(SourceStore, OwnedSubmission);
        var (requeued, discarded) = await relay.ApplyOperatorSignalsAsync(slotJobId, signals, ct);
        Assert.Equal(1, requeued);
        Assert.Equal(0, discarded);

        // The signal row is consumed, the source row is Pending with its budget reset, and the
        // evidence event carries the operator's actor key, justification, and the applied id.
        Assert.Null(await signals.GetAsync(slotJobId, OutboxSignalNames.Requeue, ct));
        var state = await Fixture.ReadOutboxRowAsync(SourceTable, row.OutboxId);
        Assert.Equal((byte)OutboxStatusCode.Pending, state.StatusCode);
        Assert.Equal(0, state.FailureCount);

        var evidence = Assert.Single(await Db.From<JobEvent>().Where(e => e.JobId == slotJobId).ToListAsync(ct));
        Assert.Equal(EventCode.OutboxRequeued, evidence.EventCode);
        Assert.Equal(ActorCode.Operator, evidence.ActorCode);
        Assert.Equal("marko", evidence.ActorKey);
        Assert.Contains("fixed the target route", evidence.ReasonMessage);
        Assert.Contains(row.OutboxId.ToString(), evidence.ReasonMessage);

        // The same pass's drain now claims the freed row and relays it into a ledger job.
        var summary = await relay.RunTickAsync(TickOptions(), ct);
        Assert.Equal(1, summary.Relayed);
        Assert.Equal(1, await CountLedgerJobsAsync(dedup, ct));
    }
}
