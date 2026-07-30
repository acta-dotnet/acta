using System.Data.Common;
using Acta;
using Acta.Kernel;
using Acta.Modules.Execution.Api;
using Acta.Modules.Execution.Jobs;
using Acta.Modules.Execution.Namespaces;
using Acta.Modules.Execution.Tenants;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>Conformance for the enqueue rejection taxonomy: suspended namespace/tenant, unknown tenant, unknown route, and retired definition map to typed reasons, and unrelated provider errors rethrow untouched.</summary>
[ConformanceSpec(
    "enqueue-rejection.taxonomy",
    "Typed enqueue rejection reasons for namespace, tenant, route, and definition",
    Area = "Enqueue",
    Contract = "Maps suspended namespace/tenant, unknown tenant, unknown route, and retired definition to EnqueueRejectedException reasons, preserving the provider exception.",
    Arrange = "The worker registers the test namespace and a suspended tenant.",
    Act = "Enqueues are attempted into a suspended namespace, with a suspended tenant, with an unknown tenant, against an unknown job, and against a retired definition.",
    Assert = "Each guarded case throws EnqueueRejectedException with the matching reason, including RouteUnknown and DefinitionRetired, and the provider exception as inner."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class EnqueueRejectionTaxonomySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    private JobEnqueueRequest Request(string jobName = "add-numbers", string? tenantKey = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new JobEnqueueRequest(TestNamespace, jobName, payload, TenantKey: tenantKey);
    }

    [Fact(DisplayName = "Enqueue into a suspended namespace throws NamespaceSuspended")]
    public async Task Namespace_suspended()
    {
        var ct = TestContext.Current.CancellationToken;
        await Services
            .GetRequiredService<INamespaceStore>()
            .SuspendNamespaceAsync(new NamespaceControlCommand(TestNamespace, new JobControlActor(JobActorCode.Operator, "op"), null), ct);
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueAsync(Request(), ct));
        Assert.Equal(EnqueueRejectionReasonCode.NamespaceSuspended, ex.Reason);
    }

    [Fact(DisplayName = "Enqueue with a suspended tenant throws TenantSuspended")]
    public async Task Tenant_suspended()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("tax-susp");
        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, ct);
        await Services.GetRequiredService<TenantsService>().SuspendAsync(key, null, null, ct);
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueAsync(Request(tenantKey: key), ct));
        Assert.Equal(EnqueueRejectionReasonCode.TenantSuspended, ex.Reason);
    }

    [Fact(DisplayName = "Enqueue with an unknown tenant throws TenantUnknown")]
    public async Task Tenant_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(Request(tenantKey: TestKey("tax-ghost")), ct)
        );
        Assert.Equal(EnqueueRejectionReasonCode.TenantUnknown, ex.Reason);
    }

    [Fact(DisplayName = "A batch into a suspended namespace throws NamespaceSuspended")]
    public async Task Batch_namespace_suspended()
    {
        var ct = TestContext.Current.CancellationToken;
        await Services
            .GetRequiredService<INamespaceStore>()
            .SuspendNamespaceAsync(new NamespaceControlCommand(TestNamespace, new JobControlActor(JobActorCode.Operator, "op"), null), ct);
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueBatchAsync([Request(), Request()], ct));
        Assert.Equal(EnqueueRejectionReasonCode.NamespaceSuspended, ex.Reason);
    }

    [Fact(DisplayName = "An unknown job rejection throws RouteUnknown")]
    public async Task Unknown_job_throws_route_unknown()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () =>
            await Jobs.EnqueueAsync(Request(jobName: "no-such-job"), ct)
        );
        Assert.Equal(EnqueueRejectionReasonCode.RouteUnknown, ex.Reason);
        Assert.IsAssignableFrom<DbException>(ex.InnerException);
    }

    [Fact(DisplayName = "Enqueue against a retired definition throws DefinitionRetired")]
    public async Task Retired_definition_throws_definition_retired()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        await Db.From<JobDefinition>()
            .Where(d => d.NamespaceId == ns && d.Name == "add-numbers")
            .UpdateOnlyAsync(() => new JobDefinition { Status = JobDefinitionStatusCode.Retired }, ct);
        var ex = await Assert.ThrowsAsync<EnqueueRejectedException>(async () => await Jobs.EnqueueAsync(Request(), ct));
        Assert.Equal(EnqueueRejectionReasonCode.DefinitionRetired, ex.Reason);
        Assert.IsAssignableFrom<DbException>(ex.InnerException);
    }
}
