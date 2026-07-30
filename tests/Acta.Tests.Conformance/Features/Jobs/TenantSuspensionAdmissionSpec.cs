using Acta.Modules.Execution.Jobs;
using Acta.Modules.Execution.Tenants;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Pins the admission-suspension semantics of tenant suspension: suspending a tenant rejects new
/// enqueues that name its key, but jobs already admitted keep their standing, and a running workflow
/// may still expand through children that inherit the suspended tenant (no explicit key, so no
/// admission check). The commit boundary is the guarantee: an enqueue transaction beginning after
/// the suspend commits is rejected; enqueues overlapping the suspend land or reject atomically.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.tenant-admission-suspension",
    "Tenant suspension is admission control, not work closure",
    Area = "Enqueue",
    Contract = "Suspension rejects new enqueues naming the tenant key while admitted workflows may expand through inherited children after the suspend commits.",
    Arrange = "A tenant is registered, a root job is admitted for it, and the tenant is then suspended.",
    Act = "A child without a key, a child naming the suspended key, and overlapping suspend and enqueue calls are attempted.",
    Assert = "The inherited child lands under the suspended tenant, explicit-key enqueues after the suspend commit reject, and overlapping enqueues land or reject atomically."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueOneAsync))]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.EnqueueBatchAsync))]
public abstract class TenantSuspensionAdmissionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool RunAsWorker => true;

    private TenantsService Tenants() => Services.GetRequiredService<TenantsService>();

    private JobEnqueueRow Row(string? tenantKey, long? parentId = null)
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new JobEnqueueRow(
            NamespaceName: TestNamespace,
            JobName: "add-numbers",
            Input: payload,
            ParentId: parentId,
            TenantKey: tenantKey
        );
    }

    [Fact(DisplayName = "An admitted workflow still creates inherited children after its tenant is suspended")]
    public async Task Inherited_child_lands_after_suspension()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-inherit");
        var tenantId = await Tenants().RegisterAsync(key, null, null, ct);

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: key)], ct);
        await Tenants().SuspendAsync(key, "hold", null, ct);

        var child = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: null, parentId: parent[0].JobId)], ct);
        var childJob = await ReadJobAsync(child[0].JobId, ct);

        Assert.NotNull(childJob);
        Assert.Equal(tenantId, childJob!.TenantId);
    }

    [Fact(DisplayName = "A child naming the suspended tenant key explicitly is rejected even under a live parent")]
    public async Task Explicit_child_key_rejects_after_suspension()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-explicit");
        await Tenants().RegisterAsync(key, null, null, ct);

        var parent = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: key)], ct);
        await Tenants().SuspendAsync(key, "hold", null, ct);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: key, parentId: parent[0].JobId)], ct)
        );
    }

    [Fact(DisplayName = "Enqueues overlapping a suspend land or reject atomically, and post-suspend enqueues reject")]
    public async Task Overlapping_suspend_and_enqueue_settle_at_the_commit_boundary()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TestKey("adm-race");
        var tenantId = await Tenants().RegisterAsync(key, null, null, ct);

        // Overlap a burst of enqueues with the suspend. Interleaving is unconstrained: each enqueue
        // either fully lands (admitted before the suspend committed) or fully rejects; nothing else.
        var enqueues = Enumerable
            .Range(0, 8)
            .Select(async _ =>
            {
                try
                {
                    await EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: key)], ct);
                    return true;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return false;
                }
            })
            .ToArray();
        await Tenants().SuspendAsync(key, "hold", null, ct);
        var landed = (await Task.WhenAll(enqueues)).Count(ok => ok);

        var stored = await Db.From<Job>().Where(j => j.TenantId == tenantId).CountAsync(ct);
        Assert.Equal(landed, stored);

        // The commit boundary: any enqueue transaction beginning after the suspend returned rejects.
        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueBatchAsync(Services, [Row(tenantKey: key)], ct));
        Assert.Equal(landed, await Db.From<Job>().Where(j => j.TenantId == tenantId).CountAsync(ct));
    }
}
