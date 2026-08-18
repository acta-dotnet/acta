using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Conformance for <c>sys.recovery</c>'s dead-worker sweep (<c>mark_dead_workers</c>), now namespace-agnostic:
/// one call flips every Active worker whose <c>last_seen_at_utc</c> is past the dead-after window to Dead
/// across all namespaces, emitting one <c>worker.died</c> event per worker into that worker's own namespace.
/// A worker is aged into the past and a positive window is used, so the sweep targets only the aged worker
/// (a fresh worker in the same shared DB survives). A worker seeded in a second namespace is reaped by the
/// same call, proving the sweep is global and that each event lands in the dead worker's namespace. An aged
/// but cleanly-stopped worker pins the status half of the predicate: going silent is what retires a worker,
/// so a worker that shut down cleanly stays Stopped however long ago it was last seen.
/// </summary>
[ConformanceSpec(
    "mark-dead-workers.global-heartbeat-sweep",
    "Stale workers in any namespace are marked Dead by one global sweep",
    Area = "Workers",
    Contract = "MarkDeadWorkers marks every stale Active worker Dead in all namespaces, writes each worker.died event to its own namespace, and skips non-Active workers.",
    Arrange = "An aged Active worker, a fresh worker and an aged Stopped worker exist in one namespace, and another aged worker exists in a second namespace.",
    Act = "A single MarkDeadWorkers.Run sweeps with a positive dead-after window and no namespace argument.",
    Assert = "Both aged Active workers are Dead with a worker.died event in their namespace, while the fresh worker stays Active and the aged Stopped worker stays Stopped."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.MarkDeadWorkersAsync))]
public abstract class MarkDeadWorkersSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int DeadAfterSeconds = 30;

    [Fact(
        DisplayName = "One global sweep marks aged workers Dead in every namespace, keeps fresh and cleanly-stopped workers, and attributes each event to the worker's namespace"
    )]
    public async Task Global_sweep_marks_aged_workers_across_namespaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsA = Runtime.RegisteredNamespaceIds[TestNamespace];

        // The registered worker in namespace A: age its last_seen far past the window.
        var workerA = await Db.From<JobWorker>().Where(w => w.NamespaceId == nsA).SingleOrDefaultAsync(ct);
        Assert.NotNull(workerA);
        Assert.Equal(WorkerStatusCode.Active, workerA!.Status);
        var agedAtA = workerA.LastHeartbeatAtUtc.AddHours(-1);
        await Db.From<JobWorker>().Where(w => w.Id == workerA.Id).UpdateOnlyAsync(() => new JobWorker { LastHeartbeatAtUtc = agedAtA }, ct);

        // A worker in a SECOND namespace: StartWorker upserts the namespace and registers the worker.
        var nsBName = TestKey("mark-dead-ns-b");
        var (nsB, workerBId) = await WorkerTestOps.StartAsync(
            Services,
            nsBName,
            ownerTeam: null,
            description: null,
            hostName: "test-host",
            deploymentVersion: "test",
            engineVersion: null,
            dotnetVersion: null,
            processId: 0,
            maxConcurrency: 1,
            ct
        );
        var agedAtB = DateTime.UtcNow.AddHours(-1);
        await Db.From<JobWorker>().Where(w => w.Id == workerBId).UpdateOnlyAsync(() => new JobWorker { LastHeartbeatAtUtc = agedAtB }, ct);

        // A fresh worker that must SURVIVE: seed one in namespace A with a current last_seen.
        var (_, freshWorkerId) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            ownerTeam: null,
            description: null,
            hostName: "fresh-host",
            deploymentVersion: "test",
            engineVersion: null,
            dotnetVersion: null,
            processId: 0,
            maxConcurrency: 1,
            ct
        );

        // A cleanly-stopped worker that must ALSO survive, aged past the same window. The sweep matches on
        // Active status AND a stale heartbeat, so aging this row leaves its status as the only thing
        // keeping it out - the assertion below is then a real negative rather than a vacuous one. A worker
        // that shut down cleanly never went silent, so retiring it as Dead would misreport why it left.
        var (_, stoppedWorkerId) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            ownerTeam: null,
            description: null,
            hostName: "stopped-host",
            deploymentVersion: "test",
            engineVersion: null,
            dotnetVersion: null,
            processId: 0,
            maxConcurrency: 1,
            ct
        );
        await Services.GetRequiredService<IWorkerStore>().StopWorkerAsync(nsA, stoppedWorkerId, ct);
        await Db.From<JobWorker>()
            .Where(w => w.Id == stoppedWorkerId)
            .UpdateOnlyAsync(() => new JobWorker { LastHeartbeatAtUtc = DateTime.UtcNow.AddHours(-1) }, ct);

        // One global sweep: no namespace argument.
        var marked = await Services.GetRequiredService<IWorkerStore>().MarkDeadWorkersAsync(DeadAfterSeconds, ct);
        Assert.True(marked >= 2, $"expected at least the two aged workers marked; got {marked}.");

        var afterA = await Db.From<JobWorker>().Where(w => w.Id == workerA.Id).SingleOrDefaultAsync(ct);
        var afterB = await Db.From<JobWorker>().Where(w => w.Id == workerBId).SingleOrDefaultAsync(ct);
        var afterFresh = await Db.From<JobWorker>().Where(w => w.Id == freshWorkerId).SingleOrDefaultAsync(ct);
        var afterStopped = await Db.From<JobWorker>().Where(w => w.Id == stoppedWorkerId).SingleOrDefaultAsync(ct);
        Assert.Equal(WorkerStatusCode.Dead, afterA!.Status);
        Assert.Equal(WorkerStatusCode.Dead, afterB!.Status);
        Assert.Equal(WorkerStatusCode.Active, afterFresh!.Status);

        // Aged but not Active: the sweep only retires a worker that went silent, so this one stays Stopped
        // and no worker.died event is written for it.
        Assert.Equal(WorkerStatusCode.Stopped, afterStopped!.Status);
        Assert.Empty(
            await Db.From<JobEvent>().Where(e => e.WorkerId == stoppedWorkerId && e.EventCode == EventCode.WorkerDied).ToListAsync(ct)
        );

        // Each worker.died event lands in the dead worker's OWN namespace.
        var eventA = Assert.Single(
            await Db.From<JobEvent>().Where(e => e.WorkerId == workerA.Id && e.EventCode == EventCode.WorkerDied).ToListAsync(ct)
        );
        var eventB = Assert.Single(
            await Db.From<JobEvent>().Where(e => e.WorkerId == workerBId && e.EventCode == EventCode.WorkerDied).ToListAsync(ct)
        );
        Assert.Equal(nsA, eventA.NamespaceId);
        Assert.Equal(nsB, eventB.NamespaceId);
        Assert.Equal(ActorCode.Worker, eventA.ActorCode);
        Assert.Equal(JobEventReasonCode.WorkerHeartbeatStale, eventA.ReasonCode);
    }
}
