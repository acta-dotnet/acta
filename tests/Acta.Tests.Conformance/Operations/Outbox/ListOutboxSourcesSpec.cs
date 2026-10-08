using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Engine.Outbox;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Outbox;

/// <summary>Conformance for the operator outbox reads: sources compose from the slot job, and the quarantine listing needs the local source.</summary>
[ConformanceSpec(
    "outbox-operator.sources",
    "Outbox sources compose from the relay slot",
    Area = "Outbox",
    Contract = "The source listing composes each namespace's relay slot, and listing quarantined messages requires the local source database."
)]
public sealed class ListOutboxSourcesSpec : OutboxRelayIntegrationBase
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
}
