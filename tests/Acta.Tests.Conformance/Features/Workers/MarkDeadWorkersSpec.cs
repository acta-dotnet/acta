using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Conformance for <c>sys.recovery</c>'s dead-worker sweep (<c>mark_dead_workers</c>), now namespace-agnostic:
/// a sweep flips every Active worker whose <c>last_seen_at_utc</c> is past the dead-after window to Dead
/// across all namespaces, emitting one <c>worker.died</c> event per worker into that worker's own namespace.
/// A worker is aged into the past and a positive window is used, so the sweep targets only the aged worker
/// (a fresh worker in the same shared DB survives). A worker seeded in a second namespace is reaped by the
/// same global sweep, proving the sweep is global and that each event lands in the dead worker's namespace.
/// The routine claims its rows with <c>FOR UPDATE SKIP LOCKED</c>, and this spec shares its schema with
/// every sibling spec sweeping the same table, so this spec re-sweeps until both aged workers settle Dead
/// rather than trusting its own single call to be the one that applied the transition - what the sweep
/// does, and the events it produces, are unchanged by how many times it is asked to run. An aged but
/// cleanly-stopped worker pins the status half of the predicate: going silent is what retires a worker, so
/// a worker that shut down cleanly stays Stopped however long ago it was last seen.
/// </summary>
[ConformanceSpec(
    "mark-dead-workers.global-heartbeat-sweep",
    "Stale workers in any namespace are marked Dead by a global sweep",
    Area = "Workers",
    Contract = "MarkDeadWorkers marks every stale Active worker Dead in all namespaces, writes each worker.died event to its own namespace, and skips non-Active workers.",
    Arrange = "An aged Active worker, a fresh worker and an aged Stopped worker exist in one namespace, and another aged worker exists in a second namespace.",
    Act = "MarkDeadWorkers.Run sweeps with a positive dead-after window and no namespace argument, repeated until both aged workers settle Dead.",
    Assert = "Both aged Active workers are Dead with a worker.died event in their namespace, while the fresh worker stays Active and the aged Stopped worker stays Stopped."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.MarkDeadWorkersAsync))]
public abstract class MarkDeadWorkersSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // 300s, comfortably above SpecWaits.Converge's 90s bound below. The convergence loop re-sweeps with
    // this same window every iteration, and the fresh worker's survival is asserted only after the loop
    // exits; if this window were close to the loop's own bound, a slow convergence could age the fresh
    // worker's heartbeat past the cutoff before the loop stops, and this spec's own re-sweep would retire
    // the very worker it asserts stays Active. The margin over Converge keeps the hang guard from ever
    // being able to falsify that assertion. The aged Active workers and the aged Stopped worker are all
    // backdated a full hour, so a 300s window leaves them past the cutoff with room to spare.
    private const int DeadAfterSeconds = 300;

    [Fact(
        DisplayName = "A global sweep marks aged workers Dead in every namespace, keeps fresh and cleanly-stopped workers, and attributes each event to the worker's namespace"
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

        // A fresh worker that must SURVIVE: seed one in namespace A with a current last_seen. Seeded
        // last so the window in which it could itself age past the sweep's own cutoff is one call wide.
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

        // Global sweep: no namespace argument. Being namespace-agnostic is the contract, so every
        // sibling spec sharing the acta_test schema sweeps these same rows, and the routine claims them
        // with FOR UPDATE SKIP LOCKED. When a sibling's concurrent sweep holds the lock on one of this
        // spec's aged workers, this call steps over that row and returns without having marked it - the
        // row still settles Dead, either because the lock holder commits the transition or because its
        // transaction rolls back and a later sweep takes the row, but not necessarily before a single
        // call returns. So this loops rather than reading once: it re-sweeps every iteration, not merely
        // re-reads, because if the lock holder rolled back, nobody else marks the row and only another
        // sweep from here will. Re-sweeping is safe because a sweep only transitions Active -> Dead, so
        // a row already Dead is not matched again - exactly one worker.died event is written per worker
        // however many sweeps run here, and the Assert.Single(...) checks below hold unchanged regardless
        // of how many iterations it took.
        var deadline = DateTime.UtcNow + SpecWaits.Converge;
        JobWorker? afterA;
        JobWorker? afterB;
        while (true)
        {
            await Services.GetRequiredService<IWorkerStore>().MarkDeadWorkersAsync(DeadAfterSeconds, ct);
            afterA = await Db.From<JobWorker>().Where(w => w.Id == workerA.Id).SingleOrDefaultAsync(ct);
            afterB = await Db.From<JobWorker>().Where(w => w.Id == workerBId).SingleOrDefaultAsync(ct);
            if (afterA?.Status == WorkerStatusCode.Dead && afterB?.Status == WorkerStatusCode.Dead)
            {
                break;
            }

            // Say that convergence was tried and for how long. Falling through to the status assertions
            // below would report only "Expected: Dead, Actual: Active", which is what this spec used to
            // fail with and reads as the sweep being broken rather than as this having waited.
            Assert.True(
                DateTime.UtcNow < deadline,
                $"aged workers never settled Dead within {SpecWaits.Converge}, re-sweeping throughout: "
                    + $"A={afterA?.Status.ToString() ?? "<row gone>"}, B={afterB?.Status.ToString() ?? "<row gone>"}."
            );
            await Task.Delay(50, ct);
        }

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
