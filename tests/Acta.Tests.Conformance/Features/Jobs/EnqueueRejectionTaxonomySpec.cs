using Acta;
using Acta.Features.Jobs;
using Acta.Features.Namespaces;
using Acta.Features.Shared;
using Acta.Features.Tenants;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>Conformance for the enqueue rejection taxonomy: suspended namespace/tenant and unknown tenant map to typed reasons, and unrelated provider errors rethrow untouched.</summary>
[ConformanceSpec(
    "enqueue-rejection.taxonomy",
    "Enqueue rejections carry a typed reason for namespace and tenant guards",
    Area = "Enqueue",
    Contract = "The enqueue facade throws EnqueueRejectedException with NamespaceSuspended/TenantSuspended/TenantUnknown reasons, and rethrows other provider errors untouched.",
    Arrange = "The worker registers the test namespace and a suspended tenant.",
    Act = "Enqueues are attempted into a suspended namespace, with a suspended tenant, with an unknown tenant, and against an unknown job.",
    Assert = "Each guarded case throws EnqueueRejectedException with the matching reason, and the unknown-job case throws a non-EnqueueRejectedException provider error."
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
        await Services.GetRequiredService<TenantsService>().RegisterAsync(key, null, null, TenantStatusCode.Suspended, ct);
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

    [Fact(DisplayName = "An unknown job rejection rethrows untouched (not EnqueueRejectedException)")]
    public async Task Unknown_job_rethrows_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await Jobs.EnqueueAsync(Request(jobName: "no-such-job"), ct));
        Assert.IsNotType<EnqueueRejectedException>(ex);
    }
}
