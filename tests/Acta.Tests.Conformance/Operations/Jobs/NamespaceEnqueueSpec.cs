using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Operations.Namespaces;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Namespace status gate at enqueue: EnqueueOne/EnqueueBatch reject when the resolved namespace is
/// suspended, and succeed again once it is reactivated. The status is flipped with a direct SQL
/// UPDATE rather than through <c>INamespaces.SuspendAsync</c>, so this spec pins the enqueue gate on
/// the persisted status alone and stays independent of the admin verb (which
/// <c>SuspendNamespaceSpec</c> covers), mirroring
/// <see cref="TenantEnqueueSpec{ConformanceFixture}"/>'s suspended-tenant coverage.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.namespace-gate",
    "Enqueue rejects a suspended namespace and resumes once reactivated",
    Area = "Enqueue",
    Contract = "EnqueueOne/EnqueueBatch reject enqueue into a suspended namespace and accept it again once the namespace is reactivated."
)]
public sealed class NamespaceEnqueueSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override bool RunAsWorker => true;

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    private JobEnqueueValues Row()
    {
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        return new JobEnqueueValues(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload);
    }

    private async Task<int> CountJobsAsync(CancellationToken ct)
    {
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        return await Db.From<JobEntity>().Where(j => j.NamespaceId == namespaceId).CountAsync(ct);
    }

    private Task SetNamespaceStatusAsync(NamespaceStatusCode status, CancellationToken ct)
    {
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        return Db.ExecuteRawAsync(
            "UPDATE {schema}.namespaces SET status_code = @p_status WHERE id = @p_id",
            ct,
            ("@p_status", (byte)status),
            ("@p_id", namespaceId)
        );
    }

    [Fact(DisplayName = "A suspended namespace rejects enqueue and persists nothing")]
    public async Task Suspended_namespace_rejects_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        await SetNamespaceStatusAsync(NamespaceStatusCode.Suspended, ct);
        var before = await CountJobsAsync(ct);

        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueBatchAsync(Services, [Row()], ct));

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "A suspended namespace rejects EnqueueOne and persists nothing")]
    public async Task Suspended_namespace_rejects_enqueue_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        await SetNamespaceStatusAsync(NamespaceStatusCode.Suspended, ct);
        var before = await CountJobsAsync(ct);

        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueOneAsync(Services, Row(), ct));

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "Enqueue succeeds again once the namespace is reactivated")]
    public async Task Reactivated_namespace_allows_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        await SetNamespaceStatusAsync(NamespaceStatusCode.Suspended, ct);
        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueBatchAsync(Services, [Row()], ct));

        await SetNamespaceStatusAsync(NamespaceStatusCode.Active, ct);
        var result = await EnqueueTestOps.EnqueueBatchAsync(Services, [Row()], ct);
        var job = await ReadJobAsync(result[0].JobId, ct);

        Assert.NotNull(job);
    }
}
