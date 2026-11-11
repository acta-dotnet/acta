using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Operations.Namespaces;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Namespace status gate at enqueue: EnqueueJob/EnqueueJobs reject when the resolved namespace is
/// suspended, and succeed again once it is reactivated. The status is flipped with a direct SQL
/// UPDATE rather than through <c>INamespaces.SuspendAsync</c>, so this spec pins the enqueue gate on
/// the persisted status alone and stays independent of the admin verb (which
/// <c>SuspendNamespaceSpec</c> covers), mirroring
/// <see cref="EnqueueJobTenantSpec{ConformanceFixture}"/>'s suspended-tenant coverage.
/// </summary>
[ConformanceSpec(
    "enqueue-jobs.namespace-gate",
    "Enqueue rejects a suspended namespace and resumes once reactivated",
    Area = "Enqueue",
    Contract = "EnqueueJob/EnqueueJobs reject a suspended namespace, accept it again once reactivated, and land a batch that passed the gate whole."
)]
[Deploys("add-numbers")]
public sealed class EnqueueJobNamespaceSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<EnqueueJobNamespaceSpec>>
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
            "UPDATE {schema}.namespaces SET namespace_status_code = @p_status WHERE namespace_id = @p_id",
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

        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueJobsAsync(Services, [Row()], ct));

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "A suspended namespace rejects EnqueueJob and persists nothing")]
    public async Task Suspended_namespace_rejects_enqueue_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        await SetNamespaceStatusAsync(NamespaceStatusCode.Suspended, ct);
        var before = await CountJobsAsync(ct);

        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueJobAsync(Services, Row(), ct));

        Assert.Equal(before, await CountJobsAsync(ct));
    }

    [Fact(DisplayName = "Enqueue succeeds again once the namespace is reactivated")]
    public async Task Reactivated_namespace_allows_enqueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        await SetNamespaceStatusAsync(NamespaceStatusCode.Suspended, ct);
        await Assert.ThrowsAnyAsync<Exception>(() => EnqueueTestOps.EnqueueJobsAsync(Services, [Row()], ct));

        await SetNamespaceStatusAsync(NamespaceStatusCode.Active, ct);
        var result = (await EnqueueTestOps.EnqueueJobsAsync(Services, [Row()], ct)).Jobs;
        var job = await ReadJobAsync(result[0].JobId, ct);

        Assert.NotNull(job);
    }

    [Fact(DisplayName = "A batch that passed the gate before a suspend committed lands whole")]
    public async Task A_batch_waiting_on_a_lane_through_a_suspend_lands_whole()
    {
        if (Db.Provider == DbProvider.Sqlite)
        {
            Assert.Skip("SQLite has one writer: the suspend cannot commit while the batch runs.");
        }
        var ct = TestContext.Current.CancellationToken;
        var lane = Unique("held-lane");
        var payload = JobPayload.Json(new AddNumbers(1, 2));
        await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", payload, Lane: lane), ct);

        // Holding the lane stops the batch after its gate and before it stages; the suspend commits in between.
        var namespaceId = Runtime.RegisteredNamespaceIds[TestNamespace];
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var blocker = await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            // By its key, as the batch locks it: a lock taken through the name index alone would not stop it.
            cmd.Transaction = blocker;
            cmd.CommandText = $"SELECT lane_id FROM {Db.Schema}.lanes WHERE namespace_id = {namespaceId} AND lane_name = '{lane}'";
            var laneId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
            cmd.CommandText =
                Db.Provider == DbProvider.SqlServer
                    ? $"SELECT lane_id FROM {Db.Schema}.lanes WITH (UPDLOCK, ROWLOCK) WHERE lane_id = {laneId}"
                    : $"SELECT lane_id FROM {Db.Schema}.lanes WHERE lane_id = {laneId} FOR UPDATE";
            Assert.NotNull(await cmd.ExecuteScalarAsync(ct));
        }

        var batch = Jobs.EnqueueJobsAsync(
                [
                    new EnqueueJobRequest(TestNamespace, "add-numbers", payload, Lane: lane),
                    new EnqueueJobRequest(TestNamespace, "add-numbers", payload),
                ],
                ct
            )
            .AsTask();
        await LockWaitProbe.WaitAsync(Db, "enqueue_jobs", LockWait.Row, batch, blocker, ct);
        await SetNamespaceStatusAsync(NamespaceStatusCode.Suspended, ct);
        await blocker.RollbackAsync(ct);

        // Staging drops no row whose namespace changed since the gate, so the batch is all or nothing.
        var outcomes = (await batch).Jobs;
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.Equal(EnqueueJobAction.Inserted, outcome.Action));
    }
}
