using Acta.Modules.Execution.Jobs;
using Acta.Modules.Execution.Tenants;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for the definition-level tenant requirement at the enqueue boundary: a Required
/// definition rejects a tenant-less row (an explicit key or an inherited parent tenant both satisfy
/// it), and a Forbidden definition rejects an explicit key and suppresses parent inheritance so its
/// rows always store <c>tenant_id NULL</c>.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.tenant-requirement",
    "The definition's tenant requirement is enforced at the enqueue boundary",
    Area = "Enqueue",
    Contract = "A Required definition rejects tenant-less rows and accepts explicit or inherited tenants while a Forbidden one rejects explicit keys and stores NULL.",
    Arrange = "Definitions declaring Required and Forbidden tenant requirements are registered along with an active tenant.",
    Act = "Roots and children are enqueued with an explicit key, with inheritance only, and with no tenant at all.",
    Assert = "Tenant-less Required rows reject, inherited tenants satisfy Required, explicit keys on Forbidden reject, and Forbidden children store tenant NULL."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class TenantRequirementEnqueueSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    private JobEnqueueRow Row(string jobName, string? tenantKey = null, long? parentId = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = jobName switch
        {
            "tenant-required-probe" => serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new TenantScopedWork("w")),
            "tenant-forbidden-probe" => serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new TenantNeutralWork("w")),
            _ => serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2)),
        };
        return new JobEnqueueRow(NamespaceName: TestNamespace, JobName: jobName, Input: payload, ParentId: parentId, TenantKey: tenantKey);
    }

    private async Task<string> RegisterTenantAsync(string hint, CancellationToken ct)
    {
        var key = TestKey(hint);
        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, ct);
        return key;
    }

    [Fact(DisplayName = "A Required definition rejects a tenant-less root")]
    public async Task Required_root_without_tenant_rejects()
    {
        var ct = TestContext.Current.CancellationToken;

        // A second benign row keeps this on the batch routine (single-row lists route to EnqueueOne).
        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-required-probe"), Row("add-numbers")], ct)
        );
    }

    [Fact(DisplayName = "A Required definition accepts a root naming an active tenant")]
    public async Task Required_root_with_tenant_lands()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterTenantAsync("req-root", ct);

        var result = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-required-probe", tenantKey: key)], ct);
        var job = await ReadJobAsync(result[0].JobId, ct);

        Assert.NotNull(job);
        Assert.NotNull(job!.TenantId);
    }

    [Fact(DisplayName = "A Required child with a tenant-scoped parent and no explicit key is satisfied by inheritance")]
    public async Task Required_child_satisfied_by_inheritance()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterTenantAsync("req-child", ct);

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("add-numbers", tenantKey: key)], ct);
        var child = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-required-probe", parentId: parent[0].JobId)], ct);
        var childJob = await ReadJobAsync(child[0].JobId, ct);

        Assert.NotNull(childJob);
        Assert.NotNull(childJob!.TenantId);
    }

    [Fact(DisplayName = "A Required child of a tenant-less parent rejects")]
    public async Task Required_child_of_tenantless_parent_rejects()
    {
        var ct = TestContext.Current.CancellationToken;

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("add-numbers")], ct);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-required-probe", parentId: parent[0].JobId)], ct)
        );
    }

    [Fact(DisplayName = "A Forbidden definition rejects a root naming a tenant")]
    public async Task Forbidden_root_with_tenant_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterTenantAsync("forb-root", ct);

        // A second benign row keeps this on the batch routine (single-row lists route to EnqueueOne).
        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-forbidden-probe", tenantKey: key), Row("add-numbers")], ct)
        );
    }

    [Fact(DisplayName = "A Forbidden child of a tenant-scoped parent lands with its inherited tenant suppressed to NULL")]
    public async Task Forbidden_child_suppresses_inheritance()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterTenantAsync("forb-child", ct);

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("add-numbers", tenantKey: key)], ct);
        // A second benign row keeps this on the batch routine (single-row lists route to EnqueueOne).
        var child = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [Row("tenant-forbidden-probe", parentId: parent[0].JobId), Row("add-numbers")],
            ct
        );
        var childJob = await ReadJobAsync(child[0].JobId, ct);

        Assert.NotNull(childJob);
        Assert.Null(childJob!.TenantId);
    }
}
