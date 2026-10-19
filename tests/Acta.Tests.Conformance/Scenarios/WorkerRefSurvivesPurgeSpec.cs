using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for worker identity across retention: an event names its worker by <c>worker_id</c> alone, with a null
/// <c>actor</c>, so the sweep keeps a terminal worker's row for as long as the events window and the joined
/// <c>workerRef</c> keeps resolving on every event that survives.
/// </summary>
[ConformanceSpec(
    "worker-ref.survives-purge",
    "A worker's events keep resolving its ref through retention",
    Area = "Retention",
    Contract = "Retention keeps a terminal worker's row while its events last, so their joined worker ref resolves, and worker events carry a null actor."
)]
public sealed class WorkerRefSurvivesPurgeSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(
        DisplayName = "Aged completion and lifecycle events still resolve their stopped worker's ref after a sweep with the default windows"
    )]
    public async Task Aged_events_resolve_their_worker_ref_after_a_default_sweep()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var execution = Services.GetRequiredService<ExecutionOperations>();
        var workerRef = WorkerRef.New();
        var (_, workerId) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            "test",
            null,
            "purge-host",
            "v1",
            null,
            null,
            4242,
            4,
            ct,
            workerRef.Value
        );

        // One attempt claimed and completed by this worker writes its job.execution-finished event.
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        var claimed = Assert.Single(
            (await execution.ClaimJobAsync(new ClaimOptions(ns, workerId, MaxBatch: 1), 30, enqueued.JobId, ct)).Jobs
        );
        Assert.Equal(
            CompleteExecutionsAction.Completed,
            (await execution.CompleteAsync(workerId, CompletionTestOps.Landing(claimed), ct)).Action
        );
        await Services.GetRequiredService<WorkersOperations>().StopWorkerAsync(ns, workerId, ct);

        // Past the default worker window, inside the default events window.
        var agedAt = DateTime.UtcNow.AddDays(-200);
        await Db.From<WorkerEntity>().Where(w => w.Id == workerId).UpdateOnlyAsync(() => new WorkerEntity { LastSeenAtUtc = agedAt }, ct);
        await Db.From<EventEntity>()
            .Where(e => e.WorkerId == workerId)
            .UpdateOnlyAsync(() => new EventEntity { CreatedAtUtc = agedAt }, ct);

        await PurgeWithDefaultWindowsAsync(ns, ct);

        Assert.NotNull(await Db.From<WorkerEntity>().Where(w => w.Id == workerId).SingleOrDefaultAsync(ct));
        var events = (
            await Operations.Ledger.ListEventsAsync(new ListEventsQuery(WorkerId: workerId, JobNamespace: TestNamespace), ct)
        ).Items;
        Assert.Equal(
            [EventCode.JobExecutionFinished, EventCode.WorkerStarted, EventCode.WorkerStopped],
            events.Select(e => e.EventCode).Order()
        );
        Assert.All(events, e => Assert.Equal<WorkerRef?>(workerRef, e.WorkerRef));
        Assert.All(events, e => Assert.Equal(ActorCode.Worker, e.ActorCode));
        Assert.All(events, e => Assert.Null(e.Actor));
    }

    [Fact(DisplayName = "A terminal worker aged past the window survives a sweep while it still leases an Executing job")]
    public async Task Terminal_worker_survives_while_it_leases_an_executing_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var (workerId, _, _) = await AgedStoppedWorkerHoldingAJobAsync(ns, ct);

        // Recovery has not reclaimed the job yet, and will name this worker when it does.
        await PurgeWithDefaultWindowsAsync(ns, ct);

        Assert.NotNull(await Db.From<WorkerEntity>().Where(w => w.Id == workerId).SingleOrDefaultAsync(ct));
    }

    [Fact(
        DisplayName = "A worker whose last heartbeat is past the window keeps its row and ref through a sweep after recovery orphans its job in its name"
    )]
    public async Task Recovery_event_keeps_its_worker_ref_through_a_sweep()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var (workerId, workerRef, jobRef) = await AgedStoppedWorkerHoldingAJobAsync(ns, ct);

        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);
        await PurgeWithDefaultWindowsAsync(ns, ct);

        Assert.NotNull(await Db.From<WorkerEntity>().Where(w => w.Id == workerId).SingleOrDefaultAsync(ct));
        var orphaned = Assert.Single(
            (await Operations.Ledger.ListEventsAsync(new ListEventsQuery(WorkerId: workerId, JobNamespace: TestNamespace), ct)).Items,
            e => e.JobRef == jobRef && e.ExecutionStatus == ExecutionStatusCode.Orphaned
        );
        Assert.Equal<WorkerRef?>(workerRef, orphaned.WorkerRef);
    }

    /// <summary>
    /// A worker that stopped while its job was still Executing, with its last heartbeat and its last write aged past
    /// the retention windows and the job's lease lapsed: the state a cluster finds when it restarts long after.
    /// </summary>
    private async Task<(int WorkerId, WorkerRef WorkerRef, JobRef JobRef)> AgedStoppedWorkerHoldingAJobAsync(int ns, CancellationToken ct)
    {
        var workerRef = WorkerRef.New();
        var (_, workerId) = await WorkerTestOps.StartAsync(
            Services,
            TestNamespace,
            "test",
            null,
            "purge-host",
            "v1",
            null,
            null,
            4242,
            4,
            ct,
            workerRef.Value
        );
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );
        Assert.Single(
            (
                await Services
                    .GetRequiredService<ExecutionOperations>()
                    .ClaimJobAsync(new ClaimOptions(ns, workerId, MaxBatch: 1), 30, enqueued.JobId, ct)
            ).Jobs
        );
        await Services.GetRequiredService<WorkersOperations>().StopWorkerAsync(ns, workerId, ct);

        // Past both default windows, worker and events.
        var agedAt = DateTime.UtcNow.AddDays(-400);
        await Db.From<WorkerEntity>()
            .Where(w => w.Id == workerId)
            .UpdateOnlyAsync(() => new WorkerEntity { LastSeenAtUtc = agedAt, ModifiedAtUtc = agedAt }, ct);
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        return (workerId, workerRef, enqueued.JobRef);
    }

    private Task PurgeWithDefaultWindowsAsync(int ns, CancellationToken ct)
    {
        var defaults = new JobsOptions();
        return RetentionTestOps.PurgeAsync(
            Services,
            ns,
            (int)defaults.JobEventsRetention.TotalDays,
            (int)defaults.AlertRetention.TotalDays,
            (int)defaults.WorkerRetention.TotalSeconds,
            1000,
            50,
            ct
        );
    }
}
