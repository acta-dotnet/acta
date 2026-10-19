using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Ledger;

/// <summary>
/// Conformance for the worker an event names: <c>events.worker_id</c> holds the id of the worker that owned the
/// transition and 0 when none did, and the list read joins it to the worker's ref, which 0 never finds.
/// </summary>
[ConformanceSpec(
    "list-job-events.worker",
    "An event stores its worker's id, or 0 when no worker owned it",
    Area = "Reads",
    Contract = "An event a worker wrote stores that worker's id and lists with its ref, and a worker-less event stores worker_id 0 and lists with no worker."
)]
public sealed class ListEventsWorkerSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "An operator cancel stores worker_id 0 and lists with no worker")]
    public async Task Operator_cancel_stores_worker_zero_and_lists_with_no_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))),
            ct
        );

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(enqueued, "operator stop", ct: ct)).Action);

        var jobRef = enqueued.JobRef.Value;
        var stored = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.JobRef == jobRef && e.EventCode == EventCode.JobCancelled).ToListAsync(ct)
        );
        Assert.Equal(0, stored.WorkerId);

        var listed = Assert.Single(
            (await Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: enqueued.JobId), ct)).Items,
            e => e.EventCode == EventCode.JobCancelled
        );
        Assert.Null(listed.WorkerId);
        Assert.Null(listed.WorkerRef);
    }

    [Fact(DisplayName = "A worker's completion stores its worker id and lists with its ref")]
    public async Task Worker_completion_stores_its_worker_id_and_lists_with_its_ref()
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
            "worker-id-host",
            "v1",
            null,
            null,
            4243,
            4,
            ct,
            workerRef.Value
        );
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(3, 4))),
            ct
        );
        var claimed = Assert.Single(
            (await execution.ClaimJobAsync(new ClaimOptions(ns, workerId, MaxBatch: 1), 30, enqueued.JobId, ct)).Jobs
        );
        Assert.Equal(
            CompleteExecutionsAction.Completed,
            (await execution.CompleteAsync(workerId, CompletionTestOps.Landing(claimed), ct)).Action
        );

        var jobRef = enqueued.JobRef.Value;
        var stored = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.JobRef == jobRef && e.EventCode == EventCode.JobExecutionFinished).ToListAsync(ct)
        );
        Assert.Equal(workerId, stored.WorkerId);

        var listed = Assert.Single(
            (await Operations.Ledger.ListEventsAsync(new ListEventsQuery(JobId: enqueued.JobId), ct)).Items,
            e => e.EventCode == EventCode.JobExecutionFinished
        );
        Assert.Equal(workerId, listed.WorkerId);
        Assert.Equal<WorkerRef?>(workerRef, listed.WorkerRef);
    }
}
