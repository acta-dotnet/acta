using Acta.Features.Jobs;
using Acta.Features.Outbox;
using Acta.Features.Workers;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acta.Tests.Conformance.Features.Outbox;

/// <summary>
/// Shared setup for the relay handoff-boundary integration specs: a live Acta ledger (a running worker
/// namespace with the <c>echo</c> route registered) plus a live per-test external-outbox source table.
/// Crash-window specs drive <see cref="OutboxRelayService"/> directly with a failure-injecting store or
/// target seam (<see cref="HookedOutboxStore"/> / <see cref="HookedJobSubmission"/>), the composition the
/// wired <c>sys.outbox</c> job uses in production. Declares no <c>[Fact]</c> so it is not itself a
/// candidate contract spec; each concrete spec adds its own metadata and guarantees.
/// </summary>
public abstract class OutboxRelayIntegrationBase<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private protected string SourceTable { get; private set; } = null!;

    private protected IOutboxRelayStore SourceStore { get; private set; } = null!;

    private protected short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    protected override async ValueTask AfterInitializeAsync()
    {
        await base.AfterInitializeAsync();
        SourceTable = "acta_outbox_hb_" + TestId;
        await Fixture.ApplyOutboxDdlAsync(SourceTable);
        SourceStore = (IOutboxRelayStore)Fixture.CreateOutboxStore(SourceTable);
    }

    private protected int MaxInlinePayloadBytes => Services.GetRequiredService<IOptions<JobsOptions>>().Value.MaxInlinePayloadBytes;

    private protected IJobSubmission OwnedSubmission => new JobsSubmission(Jobs);

    private protected OutboxRelayService Relay(IOutboxRelayStore store, IJobSubmission target) => new(store, target);

    private protected OutboxRelayTickOptions TickOptions(int quarantineThreshold = 5, int leaseTtlSeconds = 180, int? maxPayload = null) =>
        new(SourceTable, quarantineThreshold, leaseTtlSeconds, maxPayload ?? MaxInlinePayloadBytes);

    // A due producer row targeting the registered echo route in this test's namespace, staged in the past
    // so the claim predicate is satisfied without waiting.
    private protected OutboxSeed EchoRow(
        string dedup,
        string? jobName = null,
        string? jobNamespace = null,
        byte status = 10,
        int failureCount = 0,
        string? meta = null,
        byte[]? data = null,
        byte inputFormatId = 1
    )
    {
        var when = DateTime.UtcNow.AddMinutes(-5);
        return new OutboxSeed(
            OutboxId: Guid.NewGuid(),
            JobNamespace: jobNamespace ?? TestNamespace,
            JobName: jobName ?? "echo",
            InputFormatId: inputFormatId,
            InputData: data ?? EchoJson(),
            DeduplicationKey: dedup,
            PriorityCode: null,
            CreatedAtUtc: when,
            NextAttemptAtUtc: when,
            StatusCode: status,
            FailureCount: failureCount,
            Meta: meta
        );
    }

    private protected byte[] EchoJson() =>
        Services
            .GetRequiredService<IJobPayloadSerializerRegistry>()
            .Resolve(JobPayloadFormat.Json.Id)
            .Serialize(new TestJobs.Echo("relayed"))
            .Data.ToArray();

    // Ledger jobs in this test's namespace carrying the given deduplication key: the target side of the
    // (namespace, deduplication key) exactly-once invariant.
    private protected async Task<int> CountLedgerJobsAsync(string dedup, CancellationToken ct) =>
        await Db.From<Job>().Where(j => j.NamespaceId == NamespaceId && j.DeduplicationKey == dedup).CountAsync(ct);

    private protected async Task<int> CountLedgerJobsAsync(short namespaceId, string dedup, CancellationToken ct) =>
        await Db.From<Job>().Where(j => j.NamespaceId == namespaceId && j.DeduplicationKey == dedup).CountAsync(ct);

    // A second Acta runtime against the same ledger that registers TestJobsManifest under another
    // namespace, so a route unknown at first can become resolvable after a later registration.
    private protected ServiceProvider BuildRuntimeFor(string namespaceName)
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            Fixture.ApplyProvider(j, Schema.SchemaName);
            j.Run<TestJobs.TestJobsManifest>(namespaceName, ownerTeam: "test", description: GetType().FullName + ":" + namespaceName);
        });
        services.Configure<JobsOptions>(o => o.RegisterFrameworkJobs = false);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
