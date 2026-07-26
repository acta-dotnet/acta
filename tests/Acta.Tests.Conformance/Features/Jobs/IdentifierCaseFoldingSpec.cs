using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Namespaces;
using Acta.Features.Shared;
using Acta.Features.Tenants;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance: Acta equality keys normalize case-insensitively, while Acta names reject mixed-case
/// input instead of silently folding it.
/// </summary>
[ConformanceSpec(
    "identifier.normalization-boundaries",
    "Acta keys normalize to lowercase while Acta names reject mixed case",
    Area = "Enqueue",
    Contract = "Acta-owned keys are normalized to lowercase for provider-stable equality, while Acta-owned names must already be lowercase kebab/dotted-kebab.",
    Arrange = "Tenant, idempotency, and exclusive keys are prepared in mixed case while namespace and signal controls use mixed-case names.",
    Act = "Keys are written and resolved using different casing while mixed-case names are submitted at control/query boundaries.",
    Assert = "Key lookups converge on canonical lowercase rows, and mixed-case Acta names are rejected before hitting storage."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
[CoversStoreMethod(typeof(ITenantStore), nameof(ITenantStore.RegisterTenantAsync))]
public abstract class IdentifierCaseFoldingSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Enqueue-only: InitializeAsync registers the namespace + job definition so EnqueueBatch.Run
    // can resolve them. No claim/execute loop is started.
    protected override bool RunAsWorker => true;

    [Fact(DisplayName = "A tenant key differing only by case resolves to one tenant on every provider")]
    public async Task Tenant_key_is_case_insensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tk.fold-tenant");
        var upper = key.ToUpperInvariant();

        var id1 = await Services.GetRequiredService<TenantsService>().RegisterAsync(upper, null, "first", ct);
        var id2 = await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, "second", ct);

        Assert.Equal(id1, id2);

        var rows = await Db.From<Tenant>().Where(t => t.TenantKey == key).ToListAsync(ct);
        Assert.Single(rows);
        Assert.Equal(key, rows[0].TenantKey);
    }

    [Fact(DisplayName = "An deduplication key differing only by case dedups onto one job")]
    public async Task Deduplication_key_is_case_insensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("ik.fold-idem");
        var upper = key.ToUpperInvariant();

        var first = await EnqueueAsync([Row(deduplicationKey: upper)], ct);
        var second = await EnqueueAsync([Row(deduplicationKey: key)], ct);

        Assert.Equal(first[0].JobId, second[0].JobId);
        Assert.Equal(JobEnqueueAction.Deduplicated, second[0].Action);

        var stored = await Db.From<Job>().Where(j => j.Id == first[0].JobId).SingleOrDefaultAsync(ct);
        Assert.NotNull(stored);
        Assert.Equal(key, stored!.DeduplicationKey);
    }

    [Fact(DisplayName = "An exclusive key differing only by case is one mutex group")]
    public async Task Exclusive_key_is_case_insensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("ek.fold-ex");
        var upper = key.ToUpperInvariant();
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];

        await EnqueueAsync([Row(exclusiveKey: upper)], ct);
        await EnqueueAsync([Row(exclusiveKey: key)], ct);

        // Filter by namespace (unique per test run) + canonical key so reruns don't accumulate.
        var rows = await Db.From<Job>().Where(j => j.NamespaceId == namespaceId && j.ExclusiveKey == key).ToListAsync(ct);
        Assert.Equal(2, rows.Count);
    }

    [Fact(DisplayName = "Namespace filter rejects mixed case")]
    public async Task Namespace_filter_rejects_mixed_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var queries = Services.GetRequiredService<IJobs>();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await queries.ListJobsAsync(new ListJobsQuery(JobNamespace: TestNamespace.ToUpperInvariant(), JobName: "add-numbers"), ct)
        );
    }

    [Fact(DisplayName = "Deduplication-key resolve is case-insensitive (C1 guard)")]
    public async Task Deduplication_key_resolve_is_case_insensitive()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("ik.resolve-ci");
        // Write with canonical lowercase key
        var enqueued = await EnqueueAsync([Row(deduplicationKey: key)], ct);
        var expectedJobId = enqueued[0].JobId;

        // Resolve via uppercase key under the lowercase namespace; keys normalize, names do not.
        var snapshot = await Jobs.GetAsync(JobLookup.ByDeduplicationKey(TestNamespace, key.ToUpperInvariant()), ct);

        Assert.NotNull(snapshot);
        Assert.Equal(expectedJobId, snapshot!.JobId);
        Assert.Throws<ArgumentException>(() => JobLookup.ByDeduplicationKey(TestNamespace.ToUpperInvariant(), key));
    }

    [Fact(DisplayName = "Signal names reject mixed case")]
    public async Task Signal_name_rejects_mixed_case()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        // First run: suspends waiting for signal "go"
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        await Assert.ThrowsAsync<ArgumentException>(async () => await Jobs.RaiseSignalAsync(enqueued, "GO", ct: ct));

        var raise = await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);

        // Second run: signal is Set, job completes
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
    }

    private JobEnqueueRow Row(string? deduplicationKey = null, string? exclusiveKey = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new JobEnqueueRow(
            NamespaceName: TestNamespace,
            JobName: "add-numbers",
            Input: payload,
            DeduplicationKey: deduplicationKey,
            ExclusiveKey: exclusiveKey
        );
    }

    private async Task<IReadOnlyList<JobEnqueueOutcome>> EnqueueAsync(IReadOnlyList<JobEnqueueRow> rows, CancellationToken ct)
    {
        var dialect = Services.GetRequiredService<ISqlDialect>();
        return await EnqueueTestOps.EnqueueBatchAsync(Services, rows, ct);
    }
}
