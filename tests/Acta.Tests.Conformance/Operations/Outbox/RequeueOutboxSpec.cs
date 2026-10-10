using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Engine.Outbox;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the operator requeue: the command parks against the namespace's relay slot, and the next tick applies it.</summary>
[ConformanceSpec(
    "outbox-operator.requeue",
    "Requeue parks against the relay slot and the tick applies it",
    Area = "Outbox",
    Contract = "Requeue resolves the sys.outbox slot by fixed key and name, parks once and rejects while a command is pending, and a namespace without a slot is not found."
)]
public sealed class RequeueOutboxSpec : OutboxRelayIntegrationBase
{
    private IOutbox Outbox => Services.GetRequiredService<IActaOperations>().Outbox;

    // A ledger job standing in for the namespace's sys.outbox slot: resolved by the fixed
    // deduplication key and gated on the definition-supplied job name, with the runtimes row the
    // status read and the evidence event's join expect.
    private async Task<(long JobId, Guid JobRef)> SeedSlotJobAsync(string jobName, CancellationToken ct)
    {
        var seeder = new ActaTestSeeder(Db);
        var definitionId = await seeder.SeedJobDefinitionAsync(NamespaceId, name: jobName, ct: ct);
        var jobRef = Guid.NewGuid();
        var jobId = await Db.From<JobEntity>()
            .InsertAsync<long>(
                new JobEntity
                {
                    JobRef = jobRef,
                    NamespaceId = NamespaceId,
                    DefinitionId = definitionId,
                    DeduplicationKey = "sys.outbox",
                    InputFormatId = 1,
                    Input = [0],
                    AuditLevel = DefinitionAuditLevelCode.Off,
                    CreatedAtUtc = DateTime.UtcNow,
                },
                ct
            );
        await Db.From<RuntimeEntity>()
            .InsertAsync<long>(
                new RuntimeEntity
                {
                    Id = jobId,
                    NamespaceId = NamespaceId,
                    Status = JobStatusCode.Ready,
                    Priority = JobPriorityCode.Critical,
                    // Ready carries its due instant the way register_schedules writes the real
                    // slot row; ck_runtimes_ready_due rejects the pair otherwise.
                    NextRunAtUtc = DateTime.UtcNow,
                    ExecutionNumber = 1,
                },
                ct
            );
        return (jobId, jobRef);
    }

    [Fact(DisplayName = "Requeue parks once, rejects while pending, and the tick applies and frees the inbox")]
    public async Task Requeue_parks_rejects_while_pending_and_the_tick_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var dedup = Unique("facade-rq");
        var row = EchoRow(dedup, status: 90, failureCount: 5);
        await Fixture.SeedOutboxRowAsync(SourceTable, row);
        var (slotJobId, _) = await SeedSlotJobAsync("sys.outbox", ct);

        var accepted = await Outbox.RequeueAsync(TestNamespace, null, "fixed the target route", "marko", ct);
        Assert.Equal(ControlAction.Accepted, accepted.Action);
        Assert.Null(accepted.PendingSinceUtc);

        // The inbox slot is occupied and the parked command is unapplied, so a second requeue is
        // rejected carrying the pending command's park instant.
        var rejected = await Outbox.RequeueAsync(TestNamespace, [row.OutboxId], "second try", "marko", ct);
        Assert.Equal(ControlAction.Rejected, rejected.Action);
        Assert.NotNull(rejected.PendingSinceUtc);

        // The facade-minted payload is the applying tick's input: a null id scope targets every
        // quarantined row, the actor rides into the evidence event, and the consume empties the inbox.
        var signals = Services.GetRequiredService<OutboxOperations>();
        var (requeued, discarded) = await Relay(SourceStore, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, signals, ct);
        Assert.Equal(1, requeued);
        Assert.Equal(0, discarded);
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(SourceTable, row.OutboxId)).StatusCode);

        var evidence = Assert.Single(await Db.From<EventEntity>().Where(e => e.JobId == slotJobId).ToListAsync(ct));
        Assert.Equal("marko", evidence.Actor);
        Assert.Contains("fixed the target route", evidence.ReasonMessage);

        var again = await Outbox.RequeueAsync(TestNamespace, null, null, null, ct);
        Assert.Equal(ControlAction.Accepted, again.Action);
    }

    [Fact(DisplayName = "A namespace without a slot, and a user job squatting on the key, both report not found")]
    public async Task Missing_slot_and_name_mismatch_report_not_found()
    {
        var ct = TestContext.Current.CancellationToken;

        var noSlot = await Outbox.DiscardAsync(TestNamespace, null, null, null, ct);
        Assert.Equal(ControlAction.NotFound, noSlot.Action);

        // A user job reusing the "sys.outbox" deduplication key resolves by key but fails the job-name
        // gate, so it can never receive operator commands.
        await SeedSlotJobAsync("user-job", ct);
        var spoofed = await Outbox.RequeueAsync(TestNamespace, null, null, null, ct);
        Assert.Equal(ControlAction.NotFound, spoofed.Action);
    }
}
