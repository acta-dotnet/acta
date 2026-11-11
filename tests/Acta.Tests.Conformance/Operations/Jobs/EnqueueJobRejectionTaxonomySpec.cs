using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for the enqueue rejection taxonomy: suspended namespace/tenant, unknown tenant, unknown route, retired definition, and unavailable parent map to typed reasons, and unrelated provider errors rethrow untouched.</summary>
[ConformanceSpec(
    "enqueue-rejection.taxonomy",
    "Typed enqueue rejections for namespace, tenant, route, definition, and parent",
    Area = "Enqueue",
    Contract = "Maps a suspended namespace or tenant, unknown tenant or route, retired definition, and unavailable parent to typed reasons that keep the provider exception."
)]
public sealed class EnqueueJobRejectionTaxonomySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    private EnqueueJobRequest Request(string jobName = "add-numbers", string? tenantName = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new EnqueueJobRequest(TestNamespace, jobName, payload, TenantName: tenantName);
    }

    [Fact(DisplayName = "Enqueue into a suspended namespace throws NamespaceSuspended")]
    public async Task Namespace_suspended()
    {
        var ct = TestContext.Current.CancellationToken;
        await Operations.Namespaces.SuspendAsync(TestNamespace, null, "op", ct);
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueAsync(Request(), ct));
        Assert.Equal(EnqueueRejectionReason.NamespaceSuspended, ex.Reason);
    }

    [Fact(DisplayName = "Enqueue with a suspended tenant throws TenantSuspended")]
    public async Task Tenant_suspended()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("tax-susp");
        await Operations.Tenants.RegisterAsync(key, null, null, ct);
        await Operations.Tenants.SuspendAsync(key, null, null, ct);
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueAsync(Request(tenantName: key), ct));
        Assert.Equal(EnqueueRejectionReason.TenantSuspended, ex.Reason);
    }

    [Fact(DisplayName = "Enqueue with an unknown tenant throws TenantUnknown")]
    public async Task Tenant_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(Request(tenantName: Unique("tax-ghost")), ct)
        );
        Assert.Equal(EnqueueRejectionReason.TenantUnknown, ex.Reason);
    }

    [Fact(DisplayName = "A batch into a suspended namespace throws NamespaceSuspended")]
    public async Task Batch_namespace_suspended()
    {
        var ct = TestContext.Current.CancellationToken;
        await Operations.Namespaces.SuspendAsync(TestNamespace, null, "op", ct);
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueJobsAsync([Request(), Request()], ct));
        Assert.Equal(EnqueueRejectionReason.NamespaceSuspended, ex.Reason);
    }

    [Fact(DisplayName = "An unknown job rejection throws RouteUnknown")]
    public async Task Unknown_job_throws_route_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(Request(jobName: "no-such-job"), ct)
        );
        Assert.Equal(EnqueueRejectionReason.RouteUnknown, ex.Reason);
        Assert.IsAssignableFrom<DbException>(ex.InnerException);
    }

    [Fact(DisplayName = "Enqueue against a retired definition throws DefinitionRetired")]
    public async Task Retired_definition_throws_definition_retired()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == "add-numbers")
            .UpdateOnlyAsync(() => new DefinitionEntity { Status = DefinitionStatusCode.Retired }, ct);
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueAsync(Request(), ct));
        Assert.Equal(EnqueueRejectionReason.DefinitionRetired, ex.Reason);
        Assert.IsAssignableFrom<DbException>(ex.InnerException);
    }

    [Theory(DisplayName = "A child of a parent that does not exist or has finished throws ParentUnavailable and is not stored")]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData("finished", false)]
    [InlineData("finished", true)]
    public async Task Unavailable_parent_throws_parent_unavailable(string parent, bool batch)
    {
        var ct = TestContext.Current.CancellationToken;
        var parentJobId = long.MaxValue;
        if (parent == "finished")
        {
            var done = await Jobs.EnqueueAsync(Request(), ct);
            Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(done, ct));
            parentJobId = done.JobId;
        }

        var child = Request() with { ParentJobId = parentJobId };
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
        {
            if (batch)
            {
                await Jobs.EnqueueJobsAsync([child, child], ct);
            }
            else
            {
                await Jobs.EnqueueAsync(child, ct);
            }
        });

        Assert.Equal(EnqueueRejectionReason.ParentUnavailable, ex.Reason);
        Assert.IsAssignableFrom<DbException>(ex.InnerException);
        Assert.Empty(await Db.From<JobEntity>().Where(j => j.ParentJobId == parentJobId).ToListAsync(ct));
    }
}
