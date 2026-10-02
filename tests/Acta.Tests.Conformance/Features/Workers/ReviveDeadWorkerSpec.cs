using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Conformance for a heartbeat that reaches a worker already marked Dead (<c>extend_worker_leases</c>): a
/// database outage longer than the dead-after window lets <c>sys.recovery</c> mark a live fleet Dead, and
/// the first heartbeat after it makes each worker Active again, or Draining when it is mid-drain, so a later
/// clean stop still lands Stopped. The rows are set Dead directly rather than aged and swept, so no sibling
/// spec's global sweep can race the arrangement.
/// </summary>
[ConformanceSpec(
    "extend-worker-leases.revives-dead",
    "A heartbeat makes a worker marked Dead live again",
    Area = "Workers",
    Contract = "A heartbeat from a worker marked Dead makes it Active again, or Draining when it drains, and a Stopped worker stays Stopped.",
    Arrange = "Three fresh workers are registered, two of them set Dead and the third stopped cleanly.",
    Act = "Each worker heartbeats once, one of the Dead pair as draining, and that one is then stopped.",
    Assert = "The Dead pair turn Active and Draining, the Stopped one stays Stopped, and the drained one stops with one worker.stopped event."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.ExtendWorkerLeasesAsync))]
public abstract class ReviveDeadWorkerSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A heartbeat returns a Dead worker to Active, or to Draining mid-drain, and leaves a Stopped worker Stopped")]
    public async Task Heartbeat_revives_a_dead_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        var workers = Services.GetRequiredService<IWorkerStore>();
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;

        var (ns, revivedId) = await StartWorkerAsync("revived-host", ct);
        var (_, drainingId) = await StartWorkerAsync("revived-draining-host", ct);
        var (_, stoppedId) = await StartWorkerAsync("stopped-host", ct);
        await Db.From<JobWorker>()
            .Where(w => w.Id == revivedId)
            .UpdateOnlyAsync(() => new JobWorker { Status = WorkerStatusCode.Dead }, ct);
        await Db.From<JobWorker>()
            .Where(w => w.Id == drainingId)
            .UpdateOnlyAsync(() => new JobWorker { Status = WorkerStatusCode.Dead }, ct);
        await workers.StopWorkerAsync(ns, stoppedId, ct);

        await workers.ExtendWorkerLeasesAsync(revivedId, leaseTtl, false, ct);
        await workers.ExtendWorkerLeasesAsync(drainingId, leaseTtl, true, ct);
        await workers.ExtendWorkerLeasesAsync(stoppedId, leaseTtl, false, ct);

        Assert.Equal(WorkerStatusCode.Active, await StatusAsync(revivedId, ct));
        Assert.Equal(WorkerStatusCode.Draining, await StatusAsync(drainingId, ct));
        Assert.Equal(WorkerStatusCode.Stopped, await StatusAsync(stoppedId, ct));

        // Revived, the draining worker's clean stop lands: a Dead row would have refused it and written nothing.
        await workers.StopWorkerAsync(ns, drainingId, ct);
        Assert.Equal(WorkerStatusCode.Stopped, await StatusAsync(drainingId, ct));
        Assert.Single(
            await Db.From<JobEvent>().Where(e => e.WorkerId == drainingId && e.EventCode == EventCode.WorkerStopped).ToListAsync(ct)
        );
    }

    private Task<StartWorkerRow> StartWorkerAsync(string hostName, CancellationToken ct) =>
        WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            ownerTeam: null,
            description: null,
            hostName,
            deploymentVersion: "test",
            engineVersion: null,
            dotnetVersion: null,
            processId: 0,
            maxConcurrency: 1,
            ct
        );

    private async Task<WorkerStatusCode?> StatusAsync(int workerId, CancellationToken ct) =>
        (await Db.From<JobWorker>().Where(w => w.Id == workerId).SingleOrDefaultAsync(ct))?.Status;
}
