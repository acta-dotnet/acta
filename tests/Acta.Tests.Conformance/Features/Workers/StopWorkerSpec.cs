using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Workers;

/// <summary>
/// Conformance for the clean-shutdown routine (<c>stop_worker</c>): the just-registered Active worker
/// flips to Stopped and a single <c>worker.stopped</c> event lands. A second call is a no-op - the
/// worker is already terminal, so no further event is written.
/// </summary>
[ConformanceSpec(
    "stop-worker.clean-shutdown",
    "Stop flips an active worker to Stopped once and is idempotent",
    Area = "Workers",
    Contract = "Stopping an active worker flips it to Stopped and emits one worker.stopped event, and a second stop on the terminal worker is a no-op.",
    Arrange = "A just-registered worker sits Active in the test namespace.",
    Act = "StopWorker runs on the worker, then a second time on the now-terminal worker.",
    Assert = "The first stop flips the worker to Stopped with exactly one worker.stopped event and the second stop is a no-op writing nothing."
)]
[CoversStoreMethod(typeof(IWorkerStore), nameof(IWorkerStore.StopWorkerAsync))]
public abstract class StopWorkerSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(
        DisplayName = "Active worker flips to Stopped with exactly one WorkerStopped event from the Worker actor and clean-shutdown reason"
    )]
    public async Task Stops_an_active_worker_and_emits_the_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        Assert.Equal(WorkerStatusCode.Active, worker!.Status);

        await Services.GetRequiredService<IWorkerStore>().StopWorkerAsync(ns, worker.Id, ct);

        var after = await Db.From<JobWorker>().Where(w => w.Id == worker.Id).SingleOrDefaultAsync(ct);
        Assert.NotNull(after);
        Assert.Equal(WorkerStatusCode.Stopped, after!.Status);

        var events = await Db.From<JobEvent>()
            .Where(e => e.WorkerId == worker.Id && e.EventCode == EventCode.WorkerStopped)
            .ToListAsync(ct);
        var stoppedEvent = Assert.Single(events);
        Assert.Null(stoppedEvent.JobId);
        Assert.Equal(ns, stoppedEvent.NamespaceId);
        Assert.Equal(ActorCode.Worker, stoppedEvent.ActorCode);
        Assert.Equal(JobEventReasonCode.WorkerCleanShutdown, stoppedEvent.ReasonCode);
    }

    [Fact(DisplayName = "A second stop on a terminal worker is a no-op and writes no further event")]
    public async Task Second_stop_is_a_no_op_on_a_terminal_worker()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);

        await Services.GetRequiredService<IWorkerStore>().StopWorkerAsync(ns, worker!.Id, ct);
        await Services.GetRequiredService<IWorkerStore>().StopWorkerAsync(ns, worker.Id, ct);

        // The second stop is a no-op on the now-terminal worker: still exactly one worker.stopped event.
        var events = await Db.From<JobEvent>()
            .Where(e => e.WorkerId == worker.Id && e.EventCode == EventCode.WorkerStopped)
            .ToListAsync(ct);
        Assert.Single(events);
    }
}
