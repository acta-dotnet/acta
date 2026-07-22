using Acta.Features.Workers;
using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Conformance for <c>sys.recovery</c>'s dead-worker sweep (<c>mark_dead_workers</c>), now namespace-agnostic:
/// one call flips every Active worker whose <c>last_seen_at_utc</c> is past the dead-after window to Dead
/// across all namespaces, emitting one <c>worker.dead</c> event per worker into that worker's own namespace.
/// A worker is aged into the past and a positive window is used, so the sweep targets only the aged worker
/// (a fresh worker in the same shared DB survives). A worker seeded in a second namespace is reaped by the
/// same call, proving the sweep is global and that each event lands in the dead worker's namespace.
/// </summary>
[ConformanceSpec(
    "mark-dead-workers.global-heartbeat-sweep",
    "Stale workers in any namespace are marked Dead by one global sweep",
    Area = "Workers",
    Contract = "MarkDeadWorkers marks every Active worker past the dead-after window Dead across all namespaces and writes each worker.dead event to its own namespace.",
    Arrange = "An aged Active worker and a fresh worker exist in one namespace, and another aged worker exists in a second namespace.",
    Act = "A single MarkDeadWorkers.Run sweeps with a positive dead-after window and no namespace argument.",
    Assert = "Both aged workers are marked Dead with a worker.dead event in each worker's own namespace while the fresh worker stays Active."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.MarkDeadWorkersAsync))]
public abstract class MarkDeadWorkersSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const int DeadAfterSeconds = 30;

    [Fact(
        DisplayName = "One global sweep marks aged workers Dead in every namespace, keeps fresh workers, and attributes each event to the worker's namespace"
    )]
    public async Task Global_sweep_marks_aged_workers_across_namespaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var nsA = Runtime.RegisteredNamespaceIds[TestNamespace];

        // The registered worker in namespace A: age its last_seen far past the window.
        var workerA = await Db.From<JobWorker>().Where(w => w.NamespaceId == nsA).SingleOrDefaultAsync(ct);
        Assert.NotNull(workerA);
        Assert.Equal(WorkerStatusCode.Active, workerA!.Status);
        var agedAtA = workerA.LastSeenAtUtc.AddHours(-1);
        await Db.From<JobWorker>().Where(w => w.Id == workerA.Id).UpdateOnlyAsync(() => new JobWorker { LastSeenAtUtc = agedAtA }, ct);

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
        await Db.From<JobWorker>().Where(w => w.Id == workerBId).UpdateOnlyAsync(() => new JobWorker { LastSeenAtUtc = agedAtB }, ct);

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

        // One global sweep: no namespace argument.
        var marked = await Services.GetRequiredService<IWorkerStore>().MarkDeadWorkersAsync(DeadAfterSeconds, ct);
        Assert.True(marked >= 2, $"expected at least the two aged workers marked; got {marked}.");

        var afterA = await Db.From<JobWorker>().Where(w => w.Id == workerA.Id).SingleOrDefaultAsync(ct);
        var afterB = await Db.From<JobWorker>().Where(w => w.Id == workerBId).SingleOrDefaultAsync(ct);
        var afterFresh = await Db.From<JobWorker>().Where(w => w.Id == freshWorkerId).SingleOrDefaultAsync(ct);
        Assert.Equal(WorkerStatusCode.Dead, afterA!.Status);
        Assert.Equal(WorkerStatusCode.Dead, afterB!.Status);
        Assert.Equal(WorkerStatusCode.Active, afterFresh!.Status);

        // Each worker.dead event lands in the dead worker's OWN namespace.
        var eventA = Assert.Single(
            await Db.From<JobEvent>().Where(e => e.WorkerId == workerA.Id && e.EventCode == JobEventCode.WorkerDead).ToListAsync(ct)
        );
        var eventB = Assert.Single(
            await Db.From<JobEvent>().Where(e => e.WorkerId == workerBId && e.EventCode == JobEventCode.WorkerDead).ToListAsync(ct)
        );
        Assert.Equal(nsA, eventA.NamespaceId);
        Assert.Equal(nsB, eventB.NamespaceId);
        Assert.Equal(JobActorCode.Worker, eventA.ActorCode);
        Assert.Equal(JobEventReasonCode.WorkerHeartbeatStale, eventA.ReasonCode);
    }
}
