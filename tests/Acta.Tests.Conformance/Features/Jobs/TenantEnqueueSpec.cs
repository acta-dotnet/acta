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
/// Tenant resolution at enqueue: a null <c>TenantKey</c> inserts <c>tenant_id = NULL</c>, a known active
/// key resolves to its id, an unknown or suspended key rejects the whole batch atomically, a child
/// inherits its parent's tenant unless it supplies its own, and the <c>ListJobs</c> tenant filter scopes
/// results to one tenant.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.tenant-scope",
    "Enqueue resolves, inherits, rejects, and filters by tenant",
    Area = "Enqueue",
    Contract = "Enqueue resolves TenantKey to tenant_id, inherits it to children, rejects bad keys atomically, and gates cross-tenant children on an explicit override.",
    Arrange = "Active and suspended tenants are registered.",
    Act = "Jobs are enqueued with and without a tenant as roots and children, including cross-tenant children with and without the override.",
    Assert = "TenantKey resolves with children inheriting, a cross-tenant child lands only with the override, and bad keys reject the whole batch atomically."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class TenantEnqueueSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    private JobEnqueueRow Row(string? tenantKey, long? parentId = null, string? deduplicationKey = null, bool overrideParent = false)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new JobEnqueueRow(
            NamespaceName: TestNamespace,
            JobName: "add-numbers",
            Input: payload,
            DeduplicationKey: deduplicationKey,
            ParentId: parentId,
            TenantKey: tenantKey,
            OverrideParentTenant: overrideParent
        );
    }

    private async Task<int> CountJobsAsync(CancellationToken ct)
    {
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        return await Db.From<Job>().Where(j => j.NamespaceId == namespaceId).CountAsync(ct);
    }

    [Fact(DisplayName = "A null TenantKey inserts tenant_id NULL")]
    public async Task Null_tenant_inserts_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();

        var result = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: null)], ct);
        var job = await ReadJobAsync(result[0].JobId, ct);

        Assert.NotNull(job);
        Assert.Null(job!.TenantId);
    }

    [Fact(DisplayName = "A known active TenantKey resolves to its tenant id")]
    public async Task Known_tenant_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var name = TestKey("ten-known");
        var tenantId = await Services.GetRequiredService<TenantsService>().RegisterAsync(name, null, null, ct);

        var result = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: name)], ct);
        var job = await ReadJobAsync(result[0].JobId, ct);

        Assert.NotNull(job);
        Assert.Equal(tenantId, job!.TenantId);
    }

    [Fact(DisplayName = "An unknown TenantKey rejects the batch and persists nothing")]
    public async Task Unknown_tenant_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var before = await CountJobsAsync(ct);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: TestKey("ten-ghost"))], ct)
        );

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "A suspended TenantKey rejects the batch and persists nothing")]
    public async Task Suspended_tenant_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var name = TestKey("ten-off");
        await Services.GetRequiredService<TenantsService>().RegisterAsync(name, null, null, ct);
        await Services.GetRequiredService<TenantsService>().SuspendAsync(name, null, null, ct);
        var before = await CountJobsAsync(ct);

        // A second benign row keeps this on the batch routine (single-row lists route to EnqueueOne).
        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: name), Row(tenantKey: null)], ct)
        );

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "A batch with one bad tenant is rejected atomically (the good row never lands)")]
    public async Task Mixed_batch_rejects_atomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var good = TestKey("ten-good");
        await Services.GetRequiredService<TenantsService>().RegisterAsync(good, null, null, ct);
        var before = await CountJobsAsync(ct);

        JobEnqueueRow[] command = [Row(tenantKey: good), Row(tenantKey: TestKey("ten-missing"))];
        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueBatchAsync(Services, command, ct));

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "A child inherits the parent's tenant when it supplies none")]
    public async Task Child_inherits_parent_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var name = TestKey("ten-parent");
        var tenantId = await Services.GetRequiredService<TenantsService>().RegisterAsync(name, null, null, ct);

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: name)], ct);
        var child = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: null, parentId: parent[0].JobId)], ct);
        var childJob = await ReadJobAsync(child[0].JobId, ct);

        Assert.NotNull(childJob);
        Assert.Equal(tenantId, childJob!.TenantId);
    }

    [Fact(DisplayName = "A child with a different TenantKey and the explicit override lands under its own tenant")]
    public async Task Child_overrides_parent_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var parentName = TestKey("ten-p2");
        var childName = TestKey("ten-c2");
        await Services.GetRequiredService<TenantsService>().RegisterAsync(parentName, null, null, ct);
        var childTenantId = await Services.GetRequiredService<TenantsService>().RegisterAsync(childName, null, null, ct);

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: parentName)], ct);
        var child = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [Row(tenantKey: childName, parentId: parent[0].JobId, overrideParent: true)],
            ct
        );
        var childJob = await ReadJobAsync(child[0].JobId, ct);

        Assert.NotNull(childJob);
        Assert.Equal(childTenantId, childJob!.TenantId);
    }

    [Fact(DisplayName = "A child with a different TenantKey and no override is rejected atomically")]
    public async Task Child_tenant_mismatch_rejects_without_override()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var parentName = TestKey("ten-p3");
        var childName = TestKey("ten-c3");
        await Services.GetRequiredService<TenantsService>().RegisterAsync(parentName, null, null, ct);
        await Services.GetRequiredService<TenantsService>().RegisterAsync(childName, null, null, ct);

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: parentName)], ct);
        var before = await CountJobsAsync(ct);

        // A second benign row keeps this on the batch routine (single-row lists route to EnqueueOne)
        // and proves the whole batch rejects atomically.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: childName, parentId: parent[0].JobId), Row(tenantKey: null)], ct)
        );

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "A child naming its tenant-less parent's namespace tenant explicitly lands without the override")]
    public async Task Child_key_on_tenantless_parent_needs_no_override()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var childName = TestKey("ten-c4");
        var childTenantId = await Services.GetRequiredService<TenantsService>().RegisterAsync(childName, null, null, ct);

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: null)], ct);
        var child = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: childName, parentId: parent[0].JobId)], ct);
        var childJob = await ReadJobAsync(child[0].JobId, ct);

        Assert.NotNull(childJob);
        Assert.Equal(childTenantId, childJob!.TenantId);
    }

    [Fact(DisplayName = "ListJobs filters by tenant id")]
    public async Task ListJobs_filters_by_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var name = TestKey("ten-filter");
        var tenantId = await Services.GetRequiredService<TenantsService>().RegisterAsync(name, null, null, ct);

        var enqueued = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: name)], ct);
        var jobId = enqueued[0].JobId;

        var page_matched = await Services
            .GetRequiredService<IJobStore>()
            .ListJobsAsync(new JobPageRequest(TestNamespace, null, null, null, tenantId, null, null, null, null, 100, false), ct);
        var (matched, _) = (page_matched.Rows, page_matched.Total);
        Assert.Contains(matched, r => r.JobId == jobId && r.TenantId == tenantId);

        // A different (non-existent) tenant id excludes the row.
        var page_other = await Services
            .GetRequiredService<IJobStore>()
            .ListJobsAsync(new JobPageRequest(TestNamespace, null, null, null, tenantId + 100_000, null, null, null, null, 100, false), ct);
        var (other, _) = (page_other.Rows, page_other.Total);
        Assert.DoesNotContain(other, r => r.JobId == jobId);
    }
}
