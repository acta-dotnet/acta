using Acta.Relational.Entities;
using Acta.Runtime.Modules.Outbox;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Conformance for the public operator facade (<see cref="IActaOperations.Outbox"/>) over a real
/// ledger: the verbs resolve the namespace's <c>sys.outbox</c> slot by its fixed deduplication key
/// gated on the job name, park a payload the applying tick actually understands, report the
/// accepted/rejected/not-found admission outcomes, and the reads compose sources from the slot job
/// while refusing the quarantine listing where no source is registered.
/// </summary>
[ConformanceSpec(
    "outbox-operator.facade",
    "IOutbox parks accepted-then-applied commands against the namespace's relay slot",
    Area = "Outbox",
    Contract = "IOutbox resolves the sys.outbox slot by fixed key and name, parks commands the tick applies, and reports accepted, rejected, or not-found admission.",
    Arrange = "A quarantined source row and a stand-in sys.outbox slot job exist in the test namespace.",
    Act = "Operator verbs run through IActaOperations.Outbox and one operator-signal apply runs.",
    Assert = "Requeue is accepted then rejected while pending, the apply frees the row and empties the inbox, and lookups without a proper slot report not found."
)]
public abstract class OutboxOperatorFacadeSpec<TFixture> : OutboxRelayIntegrationBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private IOutbox Outbox => Services.GetRequiredService<IActaOperations>().Outbox;

    [Fact(DisplayName = "Requeue parks once, rejects while pending, and the tick applies and frees the inbox")]
    public async Task Requeue_parks_rejects_while_pending_and_the_tick_applies()
    {
        var ct = TestContext.Current.CancellationToken;
        var dedup = TestKey("facade-rq");
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
        var signals = Services.GetRequiredService<IOutboxSignalStore>();
        var (requeued, discarded) = await Relay(SourceStore, OwnedSubmission).ApplyOperatorSignalsAsync(slotJobId, signals, ct);
        Assert.Equal(1, requeued);
        Assert.Equal(0, discarded);
        Assert.Equal((byte)OutboxStatusCode.Pending, (await Fixture.ReadOutboxRowAsync(SourceTable, row.OutboxId)).StatusCode);

        var evidence = Assert.Single(await Db.From<JobEvent>().Where(e => e.JobId == slotJobId).ToListAsync(ct));
        Assert.Equal("marko", evidence.ActorKey);
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

    [Fact(DisplayName = "Sources compose from the slot job; the quarantine listing requires the local source")]
    public async Task Sources_compose_and_quarantine_listing_requires_the_local_source()
    {
        var ct = TestContext.Current.CancellationToken;

        var before = await Outbox.ListSourcesAsync(new ListOutboxSourcesQuery(JobNamespace: TestNamespace), ct);
        Assert.Empty(before.Items);

        var (_, slotJobRef) = await SeedSlotJobAsync("sys.outbox", ct);
        var sources = await Outbox.ListSourcesAsync(new ListOutboxSourcesQuery(JobNamespace: TestNamespace), ct);
        var item = Assert.Single(sources.Items);
        Assert.Equal(TestNamespace, item.JobNamespace);
        Assert.Equal(slotJobRef, item.SlotJobRef.Value);

        // No successful tick has persisted a summary, so the counters read unknown rather than zero,
        // and this host registered no relay, so the source is not locally readable.
        Assert.Null(item.LastTickSummary);
        Assert.Null(item.Backlog);
        Assert.Null(item.QuarantineTotal);
        Assert.False(item.IsLocal);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Outbox.ListQuarantinedAsync(new ListOutboxQuarantinedQuery(TestNamespace), ct)
        );
        Assert.Contains("AddOutboxRelay", error.Message);

        await Assert.ThrowsAsync<ArgumentException>(async () => await Outbox.RequeueAsync(TestNamespace, [], null, null, ct));
    }

    // A ledger job standing in for the namespace's sys.outbox slot: resolved by the fixed
    // deduplication key and gated on the definition-supplied job name, with the runtimes row the
    // status read and the evidence event's join expect.
    private async Task<(long JobId, Guid JobRef)> SeedSlotJobAsync(string jobName, CancellationToken ct)
    {
        var seeder = new ActaTestSeeder(Db);
        var definitionId = await seeder.SeedJobDefinitionAsync(NamespaceId, name: jobName, ct: ct);
        var jobRef = Guid.NewGuid();
        var jobId = await Db.From<Job>()
            .InsertAsync<long>(
                new Job
                {
                    JobRef = jobRef,
                    NamespaceId = NamespaceId,
                    DefinitionId = definitionId,
                    DeduplicationKey = "sys.outbox",
                    InputFormatId = 1,
                    Input = [0],
                    AuditLevel = JobAuditLevelCode.Off,
                    CreatedAtUtc = DateTime.UtcNow,
                },
                ct
            );
        await Db.From<JobRuntime>()
            .InsertAsync<long>(
                new JobRuntime
                {
                    Id = jobId,
                    NamespaceId = NamespaceId,
                    Status = JobStatusCode.Ready,
                    Priority = JobPriorityCode.Critical,
                    ExecutionNumber = 1,
                },
                ct
            );
        return (jobId, jobRef);
    }
}
