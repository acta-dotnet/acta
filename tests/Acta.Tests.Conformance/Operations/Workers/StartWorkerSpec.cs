using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Workers;

/// <summary>
/// Operation-level conformance for <c>StartWorker</c>: it answers nothing for an absent namespace, admits any version in
/// a namespace no deploy has recorded, and in a recorded one admits only the versions with a <c>deployments</c> row
/// unless the start does not require a deployment. It never writes the namespace row; it appends one worker row and
/// one <c>worker.started</c> event per admitted start. Calls the operation directly with a fresh unique namespace per
/// test so each start is observed in isolation.
/// </summary>
[ConformanceSpec(
    "worker.start",
    "StartWorker admits the versions a namespace allows and appends a worker row",
    Area = "Workers",
    Contract = "StartWorker admits any version until a deploy is recorded and then only deployed ones, appends one worker row and event, and never writes the namespace."
)]
public sealed class StartWorkerSpec : ActaStorageTestBase<ConformanceFixture>
{
    private Task<StartWorkerResponse?> StartAsync(
        string namespaceName,
        string deploymentVersion,
        Guid workerRef,
        CancellationToken ct,
        bool requiresDeployment = true
    ) =>
        Services
            .GetRequiredService<WorkersOperations>()
            .StartWorkerAsync(
                StartWorkerCommand.Create(namespaceName, "host1", deploymentVersion, null, null, 1001, 4, workerRef, requiresDeployment),
                ct
            );

    /// <summary>Records <paramref name="deploymentVersion"/> in the namespace the way a recorded deploy's transaction does.</summary>
    private Task<int> RecordDeploymentAsync(string namespaceName, string deploymentVersion, CancellationToken ct)
    {
        var deployments = Services.GetRequiredService<DeploymentsOperations>();
        return Db.RunInTransactionAsync(
            async (transaction, token) =>
            {
                var opened = (await deployments.StartDeploymentAsync(transaction, [namespaceName], deploymentVersion, token)).Namespaces[0];
                await deployments.RecordDeploymentAsync(
                    new(transaction, opened.NamespaceId, deploymentVersion, "test", null, opened.CurrentDeploymentVersion, false, [], []),
                    token
                );
                return opened.NamespaceId;
            },
            ct
        );
    }

    private async Task<NamespaceEntity> ReadNamespaceAsync(int namespaceId, CancellationToken ct) =>
        (await Db.From<NamespaceEntity>().Where(n => n.Id == namespaceId).SingleOrDefaultAsync(ct))!;

    private Task<int> CountWorkersAsync(Guid workerRef, CancellationToken ct) =>
        Db.From<WorkerEntity>().Where(w => w.WorkerRef == workerRef).CountAsync(ct);

    private Task<int> CountStartedEventsAsync(int namespaceId, CancellationToken ct) =>
        Db.From<EventEntity>().Where(e => e.NamespaceId == namespaceId && e.EventCode == EventCode.WorkerStarted).CountAsync(ct);

    [Fact(DisplayName = "A start in an absent namespace answers nothing and writes neither the namespace nor a worker row")]
    public async Task A_start_in_an_absent_namespace_answers_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-absent");
        var workerRef = WorkerRef.New().Value;

        Assert.Null(await StartAsync(nsName, "1.0.0", workerRef, ct));

        Assert.Null(await Db.From<NamespaceEntity>().Where(n => n.NamespaceName == nsName).SingleOrDefaultAsync(ct));
        Assert.Equal(0, await CountWorkersAsync(workerRef, ct));
    }

    [Fact(DisplayName = "A start in a namespace no deploy has recorded admits any version")]
    public async Task A_start_in_an_unrecorded_namespace_admits_any_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-open");
        var nsId = await WorkerTestOps.EnsureNamespaceAsync(Services, nsName, "test", null, ct);

        foreach (var version in new[] { "1.0.0", "2.0.0-local" })
        {
            var started = await StartAsync(nsName, version, WorkerRef.New().Value, ct);

            Assert.NotNull(started);
            Assert.Equal(nsId, started!.NamespaceId);
            Assert.Null(started.CurrentDeploymentVersion);
            Assert.NotNull(started.WorkerId);
        }
        Assert.Equal(2, await Db.From<WorkerEntity>().Where(w => w.NamespaceId == nsId).CountAsync(ct));
    }

    [Fact(DisplayName = "A start in a recorded namespace admits a version that deployed there and refuses one that did not")]
    public async Task A_start_in_a_recorded_namespace_admits_only_deployed_versions()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-gate");
        var nsId = await RecordDeploymentAsync(nsName, "1.0.0", ct);
        await RecordDeploymentAsync(nsName, "1.1.0", ct);

        // An earlier version that deployed there is admitted as well as the current one: a rollback runs.
        foreach (var version in new[] { "1.1.0", "1.0.0" })
        {
            var admitted = await StartAsync(nsName, version, WorkerRef.New().Value, ct);
            Assert.Equal((nsId, "1.1.0"), (admitted!.NamespaceId, admitted.CurrentDeploymentVersion));
            Assert.NotNull(admitted.WorkerId);
        }
        Assert.Equal(2, await CountStartedEventsAsync(nsId, ct));

        var refusedRef = WorkerRef.New().Value;
        var refused = await StartAsync(nsName, "1.2.0", refusedRef, ct);

        Assert.Equal((nsId, (int?)null, "1.1.0"), (refused!.NamespaceId, refused.WorkerId, refused.CurrentDeploymentVersion));
        Assert.Equal(0, await CountWorkersAsync(refusedRef, ct));
        Assert.Equal(2, await CountStartedEventsAsync(nsId, ct));
    }

    [Fact(DisplayName = "A start that does not require a deployment admits a version that never deployed in the namespace")]
    public async Task A_start_that_does_not_require_a_deployment_admits_any_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-debug");
        var nsId = await RecordDeploymentAsync(nsName, "1.0.0", ct);
        var workerRef = WorkerRef.New().Value;

        var started = await StartAsync(nsName, "2.0.0", workerRef, ct, requiresDeployment: false);

        Assert.Equal((nsId, "1.0.0"), (started!.NamespaceId, started.CurrentDeploymentVersion));
        var worker = Assert.Single(await Db.From<WorkerEntity>().Where(w => w.WorkerRef == workerRef).ToListAsync(ct));
        Assert.Equal((started.WorkerId, "2.0.0"), ((int?)worker.Id, worker.DeploymentVersion));
        Assert.Equal(1, await CountStartedEventsAsync(nsId, ct));
    }

    [Fact(DisplayName = "A start leaves the namespace row as it found it, whether it admits or refuses the version")]
    public async Task A_start_never_writes_the_namespace_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var openId = await WorkerTestOps.EnsureNamespaceAsync(Services, Unique("sw-keep-open"), "team-a", "desc", ct);
        var recordedName = Unique("sw-keep-recorded");
        var recordedId = await RecordDeploymentAsync(recordedName, "1.0.0", ct);
        var openBefore = await ReadNamespaceAsync(openId, ct);
        var recordedBefore = await ReadNamespaceAsync(recordedId, ct);

        await StartAsync(openBefore.NamespaceName, "1.0.0", WorkerRef.New().Value, ct);
        await StartAsync(recordedName, "1.0.0", WorkerRef.New().Value, ct);
        await StartAsync(recordedName, "9.9.9", WorkerRef.New().Value, ct);

        foreach (
            var (before, after) in new[]
            {
                (openBefore, await ReadNamespaceAsync(openId, ct)),
                (recordedBefore, await ReadNamespaceAsync(recordedId, ct)),
            }
        )
        {
            Assert.Equal(
                (before.Version, before.ModifiedAtUtc, before.DeploymentVersion, before.OwnerTeam, before.Description),
                (after.Version, after.ModifiedAtUtc, after.DeploymentVersion, after.OwnerTeam, after.Description)
            );
        }
    }

    [Fact(DisplayName = "Each StartWorker call returns a distinct worker id and leaves a distinct row in the namespace")]
    public async Task Worker_insert_is_append_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-aonly");

        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host1", "v1", null, null, 1001, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host2", "v1", null, null, 1002, 4, ct);
        var (_, wId3) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host3", "v1", null, null, 1003, 4, ct);

        Assert.NotEqual(wId1, wId2);
        Assert.NotEqual(wId2, wId3);
        Assert.NotEqual(wId1, wId3);

        var count = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == nsId).CountAsync(ct);
        Assert.Equal(3, count);
    }

    [Fact(DisplayName = "Each worker has exactly one WorkerStarted event with itself as the subject and no details")]
    public async Task Each_worker_has_exactly_one_WorkerStarted_event_with_itself_as_subject()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-evt");
        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host1", "v1", null, null, 1001, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "host2", "v1", null, null, 1002, 4, ct);

        // The worker is the subject and the writer both; the event needs no actor to name it.
        foreach (var workerId in new[] { wId1, wId2 })
        {
            var started = Assert.Single(
                await Db.From<EventEntity>().Where(e => e.WorkerId == workerId && e.EventCode == EventCode.WorkerStarted).ToListAsync(ct)
            );
            Assert.Equal((EntityCode.Worker, (long)workerId), (started.EntityCode, started.EntityId));
            Assert.Null(started.Details);
            Assert.Equal(nsId, started.NamespaceId);
        }
    }

    [Fact(DisplayName = "Same host and process_id on two calls yields two distinct worker ids and two rows (no dedup)")]
    public async Task Duplicate_host_and_process_id_appends_two_distinct_workers()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsName = Unique("sw-dup");

        var (nsId, wId1) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "same-host", "v1", null, null, 9999, 4, ct);
        var (_, wId2) = await WorkerTestOps.StartAsync(Services, nsName, "test", null, "same-host", "v1", null, null, 9999, 4, ct);

        Assert.NotEqual(wId1, wId2);

        var workers = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == nsId).ToListAsync(ct);
        Assert.Equal(2, workers.Count);
        Assert.Equal(2, workers.Select(w => w.Id).Distinct().Count());
    }

    [Fact(DisplayName = "A start in the namespace named 'sys' is rejected while the seeded sys namespace remains intact")]
    public async Task A_start_in_sys_is_rejected_and_the_seeded_namespace_survives()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => StartAsync("sys", "v1", WorkerRef.New().Value, ct));

        var page = await Services.GetRequiredService<INamespaces>().ListAsync(new ListNamespacesQuery(NameContains: "sys"), ct);
        Assert.Contains(page.Items, item => item.JobNamespace == "sys");
    }
}
