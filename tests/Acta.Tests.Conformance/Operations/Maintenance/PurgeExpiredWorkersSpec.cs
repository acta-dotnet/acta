using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Maintenance;

/// <summary>
/// Conformance for <c>sys.retention</c>'s worker delete (<c>PurgeExpiredWorkers</c>): terminal (Stopped or Dead)
/// <c>workers</c> rows past the one retention window go, and an Active worker stays whatever the window.
/// </summary>
[ConformanceSpec(
    "purge-expired-workers.sweeps",
    "Purge deletes stopped and dead workers past the window",
    Area = "Retention",
    Contract = "Purge deletes Stopped and Dead workers past the retention window and never an Active one."
)]
[Deploys("add-numbers")]
public sealed class PurgeExpiredWorkersSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<PurgeExpiredWorkersSpec>>
{
    [Fact(DisplayName = "Stopped and Dead workers are both reaped while an Active worker is kept")]
    public async Task Terminal_workers_are_deleted_and_active_worker_survives()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workers = Services.GetRequiredService<WorkersOperations>();

        // The runtime's own worker is the only row here, and it is Active: read it before seeding the
        // second one so the single-row query still addresses it unambiguously.
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        Assert.Equal(WorkerStatusCode.Active, worker!.Status);

        // Active workers are never purged, even with the cutoff in the future.
        var active = await RetentionTestOps.PurgeAsync(Services, ns, -1, 1000, 50, ct);
        Assert.Equal(0, active.Workers);
        Assert.NotNull(await Db.From<WorkerEntity>().Where(w => w.Id == worker.Id).SingleOrDefaultAsync(ct));

        // A second worker that shuts down cleanly: Stopped is the other terminal status, and it reaches
        // it without ever being Dead - mark_dead_workers only retires a worker that went silent.
        var (_, stoppedId) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            "test",
            null,
            "purge-host",
            "v1",
            null,
            null,
            4243,
            4,
            ct
        );
        await workers.StopWorkerAsync(ns, stoppedId, ct);

        // Retire the first one: age last_seen an hour back, then the global sweep flips it to Dead. The
        // sweep reaches every namespace, so its window stays far above the few minutes other specs age
        // their live workers by. The cleanly-stopped row is untouched by that sweep and stays Stopped.
        var agedAt = DateTime.UtcNow.AddHours(-1);
        await Db.From<WorkerEntity>().Where(w => w.Id == worker.Id).UpdateOnlyAsync(() => new WorkerEntity { LastSeenAtUtc = agedAt }, ct);
        await workers.MarkDeadWorkersAsync(1800, ct);
        Assert.Equal(
            WorkerStatusCode.Stopped,
            (await Db.From<WorkerEntity>().Where(w => w.Id == stoppedId).SingleOrDefaultAsync(ct))!.Status
        );

        // The sweep takes both terminal rows: the window applies to Stopped and Dead alike. Drained, because a single
        // sweep passes over a row another transaction holds under READPAST.
        var purged = await RetentionTestOps.PurgeUntilAsync(
            Services,
            ns,
            -1,
            1000,
            50,
            async () => (await Db.From<WorkerEntity>().Where(w => w.NamespaceId == ns).ToListAsync(ct)).Count == 0,
            ct
        );
        Assert.Equal(2, purged.Workers);
        Assert.Null(await Db.From<WorkerEntity>().Where(w => w.Id == worker.Id).SingleOrDefaultAsync(ct));
        Assert.Null(await Db.From<WorkerEntity>().Where(w => w.Id == stoppedId).SingleOrDefaultAsync(ct));
    }
}
