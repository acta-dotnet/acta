using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The worker-shutdown drain contract, shared across every <see cref="ExecutionProfile"/>: a graceful
/// stop must never finalize an in-flight job. When the worker token is cancelled while a handler is mid
/// execution, the executing row is left Executing with its lease intact and NO completion is written, so
/// <c>sys.recovery</c> reclaims it after the lease lapses and the job re-runs - the documented
/// worker-shutdown -&gt; retry/reclaim contract.
/// </summary>
/// <remarks>
/// The trap this guards is the Bulk profile. A shutdown-cancelled handler maps to
/// <c>ExecutionOutcome.Failed</c> with no reason, which is the exact shape Bulk buffers for group commit -
/// and the flusher commits under <see cref="System.Threading.CancellationToken.None"/>, so without the
/// runner's worker-shutdown guard a routine restart would group-commit every in-flight job as a terminal
/// Failed. Buffered/Direct never buffered (their inline completion throws on the cancelled token and is
/// abandoned), but the guard makes all three profiles behave identically and explicitly. Concrete
/// per-profile specs derive from this base; SQLite has no completion routine, so Bulk there runs as Direct.
/// </remarks>
public abstract class WorkerShutdownDrainSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // A hang guard, not a measurement: these specs drive the real claim loop and its DB round-trips,
    // which run slow under the suite's cross-test parallelism and finish well under a second in
    // isolation. Nothing here asserts on how long the drain took. See SpecWaits.
    private static readonly TimeSpan Timeout = SpecWaits.Gate;

    /// <summary>The profile under test; each concrete spec pins one.</summary>
    protected abstract ExecutionProfile Profile { get; }

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = Profile;
            // One completion per batch so a Bulk flusher commits immediately during the drain - the exact
            // window where the unguarded bug would group-commit a shutdown-cancelled attempt as Failed.
            o.BatchCompletionSize = 1;
        });
    }

    // Drive the real claim/dispatch loop under a worker-scoped token (the token base.StopAsync cancels on a
    // graceful host stop), block a handler in-flight, then cancel the token to simulate the shutdown and
    // await the drain. Returns the enqueued job once the loop has fully unwound.
    private async Task<JobEnqueueOutcome> RunInFlightThenShutdownAsync(CancellationToken ct)
    {
        CancellableHandler.Reset(TestNamespace);
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "cancellable", JobPayload.None), ct);

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

    protected async Task ShutdownLeavesInFlightJobReclaimableAsync(CancellationToken ct)
    {
        var enqueued = await RunInFlightThenShutdownAsync(ct);

        // The worker wrote NO completion: the row is still Executing and still leased, exactly the
        // reclaimable state sys.recovery keys on. Pre-fix, Bulk would have group-committed a terminal Failed
        // here (status 200, one execution-finished event) - this is the assertion that catches it.
        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, after.Status);
        Assert.NotNull(after.LeasedByWorkerId);
        Assert.Equal((short)0, after.FailureCount);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct));
    }

    protected async Task ShutdownAbandonedJobIsReclaimedAndRetriedAsync(CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await RunInFlightThenShutdownAsync(ct);

        // The job is Executing with a live lease. In production the heartbeat stopped at shutdown, so the
        // lease simply runs out; lapse it deterministically, then sweep sys.recovery.
        await ChaosSpecHelpers.ExpireLeaseAsync(Db, enqueued.JobId, ct);
        Assert.Equal(1, await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct));

        // Reclaim re-armed it to Ready (a retry) with failure_count bumped and an Orphaned execution-finished
        // event - never the terminal Failed the unguarded Bulk path would have stamped.
        var reclaimed = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.Null(reclaimed.LeasedByWorkerId);
        Assert.Equal((short)1, reclaimed.FailureCount);
        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Orphaned, ct));
    }
}

/// <summary>Worker-shutdown drain under the Buffered (channel-dispatch) profile.</summary>
[ConformanceSpec(
    "runtime.worker-shutdown-drain.standard",
    "Buffered worker stop leaves an in-flight job reclaimable, never Failed",
    Area = "Recovery",
    Contract = "Under the Buffered profile a graceful worker stop with a job in-flight writes no completion and leaves it Executing for recovery, never terminal Failed.",
    Arrange = "A worker runs the Buffered profile with a handler that blocks in-flight until its token is cancelled.",
    Act = "The worker token is cancelled mid-execution and the loop drains, after which the lapsed lease is swept by recovery.",
    Assert = "The job is left Executing with no completion written and recovery re-arms it to Ready, never terminal Failed."
)]
public abstract class BufferedWorkerShutdownDrainSpec<TFixture> : WorkerShutdownDrainSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    protected override ExecutionProfile Profile => ExecutionProfile.Buffered;

    [Fact(DisplayName = "Buffered: a worker stop with a job in-flight leaves it Executing for recovery and writes no terminal completion")]
    public Task Worker_shutdown_leaves_an_in_flight_job_reclaimable() =>
        ShutdownLeavesInFlightJobReclaimableAsync(TestContext.Current.CancellationToken);

    [Fact(DisplayName = "Buffered: after a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)")]
    public Task Shutdown_abandoned_job_is_reclaimed_and_retried() =>
        ShutdownAbandonedJobIsReclaimedAndRetriedAsync(TestContext.Current.CancellationToken);
}

/// <summary>Worker-shutdown drain under the Direct (combined claim-execute) profile.</summary>
[ConformanceSpec(
    "runtime.worker-shutdown-drain.fast",
    "Direct worker stop leaves an in-flight job reclaimable, never Failed",
    Area = "Recovery",
    Contract = "Under the Direct profile a graceful worker stop with a job in-flight writes no completion and leaves it Executing for recovery, never terminal Failed.",
    Arrange = "A worker runs the Direct profile with a handler that blocks in-flight until its token is cancelled.",
    Act = "The worker token is cancelled mid-execution and the loop drains, after which the lapsed lease is swept by recovery.",
    Assert = "The job is left Executing with no completion written and recovery re-arms it to Ready, never terminal Failed."
)]
public abstract class DirectWorkerShutdownDrainSpec<TFixture> : WorkerShutdownDrainSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    protected override ExecutionProfile Profile => ExecutionProfile.Direct;

    [Fact(DisplayName = "Direct: a worker stop with a job in-flight leaves it Executing for recovery and writes no terminal completion")]
    public Task Worker_shutdown_leaves_an_in_flight_job_reclaimable() =>
        ShutdownLeavesInFlightJobReclaimableAsync(TestContext.Current.CancellationToken);

    [Fact(DisplayName = "Direct: after a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)")]
    public Task Shutdown_abandoned_job_is_reclaimed_and_retried() =>
        ShutdownAbandonedJobIsReclaimedAndRetriedAsync(TestContext.Current.CancellationToken);
}

/// <summary>
/// Worker-shutdown drain under the Bulk (group-commit) profile - the profile whose buffered completion path
/// the runner's worker-shutdown guard exists to protect.
/// </summary>
[ConformanceSpec(
    "runtime.worker-shutdown-drain.bulk",
    "Bulk worker stop never group-commits an in-flight job as Failed",
    Area = "Recovery",
    Contract = "Under the Bulk profile a graceful worker stop with a job in-flight buffers no completion and leaves it Executing for recovery, never a group-committed Failed.",
    Arrange = "A worker runs the Bulk profile with a one-row completion batch and a handler that blocks in-flight until its token is cancelled.",
    Act = "The worker token is cancelled mid-execution and the drain and flusher complete, after which the lapsed lease is swept by recovery.",
    Assert = "The job is left Executing with no group-committed completion and recovery re-arms it to Ready, never terminal Failed."
)]
public abstract class BulkWorkerShutdownDrainSpec<TFixture> : WorkerShutdownDrainSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    protected override ExecutionProfile Profile => ExecutionProfile.Bulk;

    [Fact(DisplayName = "Bulk: a worker stop with a job in-flight leaves it Executing for recovery and group-commits no terminal Failed")]
    public Task Worker_shutdown_leaves_an_in_flight_job_reclaimable() =>
        ShutdownLeavesInFlightJobReclaimableAsync(TestContext.Current.CancellationToken);

    [Fact(DisplayName = "Bulk: after a worker stop, recovery reclaims the abandoned in-flight job back to Ready (it retries)")]
    public Task Shutdown_abandoned_job_is_reclaimed_and_retried() =>
        ShutdownAbandonedJobIsReclaimedAndRetriedAsync(TestContext.Current.CancellationToken);
}
