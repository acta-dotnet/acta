using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The worker-shutdown drain contract: a graceful
/// stop must never finalize an in-flight job. When the worker token is cancelled while a handler is mid
/// execution, the executing row is left Executing with its lease intact and NO completion is written, so
/// <c>sys.recovery</c> reclaims it after the lease lapses and the job re-runs - the documented
/// worker-shutdown -&gt; retry/reclaim contract.
/// </summary>
/// <remarks>
/// A shutdown-cancelled handler maps to <c>CompletionKind.Failed</c> with no reason, so without the runner's
/// worker-shutdown guard a routine restart would finalize every in-flight job as terminal Failed.
/// </remarks>
[ConformanceSpec(
    "runtime.worker-shutdown-drain",
    "A worker stop leaves an in-flight job reclaimable, never Failed",
    Area = "Recovery",
    Contract = "A graceful worker stop with a job in-flight writes no completion and leaves it Executing for recovery, never terminal Failed."
)]
public sealed class WorkerShutdownDrainSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // A hang guard, not a measurement: these specs drive the real claim loop and its DB round-trips,
    // which run slow under the suite's cross-test parallelism and finish well under a second in
    // isolation. Nothing here asserts on how long the drain took. See SpecWaits.
    private static readonly TimeSpan Timeout = SpecWaits.Gate;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => { });
    }

    // Drive the real claim/dispatch loop under a worker-scoped token (the token base.StopAsync cancels on a
    // graceful host stop), block a handler in-flight, then cancel the token to simulate the shutdown and
    // await the drain. Returns the enqueued job once the loop has fully unwound.
    private async Task<EnqueueJobResponse> RunInFlightThenShutdownAsync(CancellationToken ct)
    {
        CancellableHandler.Reset(TestNamespace);
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "cancellable", JobPayload.None), ct);

        using var workerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(workerCts.Token);

        // The handler has entered: the job is claimed and Executing.
        await CancellableHandler.Started(TestNamespace).WaitAsync(Timeout, ct);

        // Graceful shutdown: cancel the worker token and let the loop drain its in-flight handler.
        await workerCts.CancelAsync();
        await loop.WaitAsync(Timeout, ct);

        // The handler observed cancellation of its token and unwound cooperatively.
        Assert.True(await CancellableHandler.Observed(TestNamespace).WaitAsync(Timeout, ct));
        return enqueued;
    }

    private async Task ShutdownLeavesInFlightJobReclaimableAsync(CancellationToken ct)
    {
        var enqueued = await RunInFlightThenShutdownAsync(ct);

        // The worker wrote NO completion: the row is still Executing and still leased, exactly the
        // reclaimable state sys.recovery keys on, not a terminal Failed with an execution-finished event.
        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, after.Status);
        Assert.NotNull(after.LeasedByWorkerId);
        Assert.Equal(0, after.FailureCount);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct));
    }

    private async Task ShutdownAbandonedJobIsReclaimedAndRetriedAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await RunInFlightThenShutdownAsync(ct);

        // The job is Executing with a live lease. In production the heartbeat stopped at shutdown, so the
        // lease simply runs out; lapse it deterministically, then sweep sys.recovery.
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));

        // Reclaim re-armed it to Ready (a retry) with failure_count bumped and an Orphaned execution-finished
        // event, never a terminal Failed.
        var reclaimed = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.Null(reclaimed.LeasedByWorkerId);
        Assert.Equal(1, reclaimed.FailureCount);
        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Orphaned, ct));
    }

    [Fact(DisplayName = "A worker stop with a job in-flight leaves it Executing for recovery and writes no terminal completion")]
    public Task Worker_shutdown_leaves_an_in_flight_job_reclaimable() =>
        ShutdownLeavesInFlightJobReclaimableAsync(TestContext.Current.CancellationToken);

    [Fact(DisplayName = "After a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)")]
    public Task Shutdown_abandoned_job_is_reclaimed_and_retried() =>
        ShutdownAbandonedJobIsReclaimedAndRetriedAsync(TestContext.Current.CancellationToken);
}
