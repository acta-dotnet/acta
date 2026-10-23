using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

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
    Contract = "A Required definition rejects tenant-less rows and accepts explicit or inherited tenants while a Forbidden one rejects explicit keys and stores NULL."
)]
public sealed class TenantRequirementEnqueueSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    private JobEnqueueValues Row(string jobName, string? tenantName = null, long? parentId = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = jobName switch
        {
            "tenant-required-probe" => serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new TenantScopedWork("w")),
            "tenant-forbidden-probe" => serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new TenantNeutralWork("w")),
            _ => serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2)),
        };
        return new JobEnqueueValues(
            NamespaceName: TestNamespace,
            JobName: jobName,
            Input: payload,
            ParentJobId: parentId,
            TenantName: tenantName
        );
    }

    private async Task<string> RegisterTenantAsync(string hint, CancellationToken ct)
    {
        var key = Unique(hint);
        await Operations.Tenants.RegisterAsync(key, null, null, ct);
        return key;
    }

    [Fact(DisplayName = "A Required definition rejects a tenant-less root")]
    public async Task Required_root_without_tenant_rejects()
    {
        var ct = TestContext.Current.CancellationToken;

        // A second benign row keeps this on the batch routine (single-row lists route to EnqueueJob).
        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-required-probe"), Row("add-numbers")], ct)
        );
    }

    [Fact(DisplayName = "A Required definition accepts a root naming an active tenant")]
    public async Task Required_root_with_tenant_lands()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterTenantAsync("req-root", ct);

        var result = (await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-required-probe", tenantName: key)], ct)).Jobs;
        var job = await ReadJobAsync(result[0].JobId, ct);

        Assert.NotNull(job);
        Assert.NotNull(job!.TenantId);
    }

    [Fact(DisplayName = "A Required child with a tenant-scoped parent and no explicit key is satisfied by inheritance")]
    public async Task Required_child_satisfied_by_inheritance()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterTenantAsync("req-child", ct);

        var parent = (await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("add-numbers", tenantName: key)], ct)).Jobs;
        var child = (await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-required-probe", parentId: parent[0].JobId)], ct)).Jobs;
        var childJob = await ReadJobAsync(child[0].JobId, ct);

        Assert.NotNull(childJob);
        Assert.NotNull(childJob!.TenantId);
    }

    [Fact(DisplayName = "A Required child of a tenant-less parent rejects")]
    public async Task Required_child_of_tenantless_parent_rejects()
    {
        var ct = TestContext.Current.CancellationToken;

        var parent = (await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("add-numbers")], ct)).Jobs;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-required-probe", parentId: parent[0].JobId)], ct)
        );
    }

    [Fact(DisplayName = "A Forbidden definition rejects a root naming a tenant")]
    public async Task Forbidden_root_with_tenant_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterTenantAsync("forb-root", ct);

        // A second benign row keeps this on the batch routine (single-row lists route to EnqueueJob).
        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row("tenant-forbidden-probe", tenantName: key), Row("add-numbers")], ct)
        );
    }

    [Fact(DisplayName = "A Forbidden child of a tenant-scoped parent lands with its inherited tenant suppressed to NULL")]
    public async Task Forbidden_child_suppresses_inheritance()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = await RegisterTenantAsync("forb-child", ct);

        var parent = (await EnqueueTestOps.EnqueueBatchAsync(Services, [Row("add-numbers", tenantName: key)], ct)).Jobs;
        // A second benign row keeps this on the batch routine (single-row lists route to EnqueueJob).
        var child = (
            await EnqueueTestOps.EnqueueBatchAsync(
                Services,
                [Row("tenant-forbidden-probe", parentId: parent[0].JobId), Row("add-numbers")],
                ct
            )
        ).Jobs;
        var childJob = await ReadJobAsync(child[0].JobId, ct);

        Assert.NotNull(childJob);
        Assert.Null(childJob!.TenantId);
    }
}
