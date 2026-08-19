using System.Diagnostics;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// The graceful-drain contract, shared across every <see cref="ExecutionProfile"/>: a stop flips the worker
/// Active -&gt; Draining (via the heartbeat, no dedicated routine), keeps the in-flight handler running under
/// the live host token until it finishes, then stamps Stopped. The distinguishing guarantee versus a hard
/// stop is that the in-flight job lands Succeeded - it is NOT cancelled and left for reclaim.
/// </summary>
/// <remarks>
/// Drives the runtime directly: <see cref="WorkerRuntime.RunAsync"/> runs the claim loop and heartbeat,
/// <see cref="WorkerRuntime.BeginDrainAsync"/> begins the drain, and a gate handler holds the job in-flight
/// until the test releases it so the Draining window is observable. Concrete per-profile specs derive from
/// this base; SQLite has no completion routine, so Bulk there runs as Direct.
/// </remarks>
public abstract class WorkerDrainSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // A hang guard, not a measurement: these specs drive the real claim loop and its DB round-trips,
    // which run slow under the suite's cross-test parallelism and finish well under a second in
    // isolation. Nothing here asserts on how long the drain took. See SpecWaits.
    private static readonly TimeSpan Timeout = SpecWaits.Gate;

    protected abstract ExecutionProfile Profile { get; }

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.ExecutionProfile = Profile;
            o.BatchCompletionSize = 1;

            // The real mechanism this used to paper over: WorkerHeartbeat.TickAsync built its "live" set
            // from the extend-leases queries, then reconciled whatever was in RunningAttempts by the time
            // those queries returned - so a job claimed and dispatched into RunningAttempts mid-tick, after
            // the extend query had already started, was invisible to that tick's live set and got wrongly
            // cancelled (Executing -> Failed). Fixed in WorkerHeartbeat by reconciling only a snapshot of
            // RunningAttempts taken before the extend queries run. A long lease + dead-after window is kept
            // here regardless, to absorb unrelated timing noise from the suite's aggressive cross-test
            // parallelism (a starved heartbeat tick could otherwise let a lease lapse and be reclaimed).
            // Test-only; production leases stay short. Invariants: LeaseTtl >= 2x heartbeat, WorkerDeadAfter
            // >= 3x heartbeat and > LeaseTtl.
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            o.LeaseTtlSeconds = 120;
            o.WorkerDeadAfter = TimeSpan.FromSeconds(300);
        });
    }

    protected async Task DrainFinishesInFlightThenStopsAsync(CancellationToken ct)
    {
        DrainGate.Reset(TestNamespace);
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "drain-gate", JobPayload.None), ct);

        // Run the full runtime (claim loop + heartbeat) under a host token, the token a hard stop would cancel.
        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = Runtime.RunAsync(hostCts.Token);

        // The job is claimed and the handler is in-flight under an Active worker.
        await DrainGate.Entered(TestNamespace).WaitAsync(Timeout, ct);
        Assert.Equal(WorkerStatusCode.Active, (await ReadWorkerAsync(ns, ct)).Status);

        // Begin draining: stamp Draining and stop claiming, but the in-flight handler keeps running.
        await Runtime.BeginDrainAsync(ct);

        Assert.Equal(WorkerStatusCode.Draining, (await ReadWorkerAsync(ns, ct)).Status);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, Runtime.InFlightCount);

        // Let the handler finish: the drain runs it to completion (Succeeded), never cancel-and-reclaim.
        DrainGate.Release(TestNamespace);
        await WaitForStatusAsync(enqueued.JobId, JobStatusCode.Succeeded, ct);
        // Poll, not an instant assert: the inline-completion profiles write Succeeded to the DB just before the
        // attempt is removed from RunningAttempts, so the count can lag the committed Succeeded read by a tick.
        await WaitForInFlightZeroAsync(ct);

        // Now stamp Stopped (Draining -> Stopped), then tear down the heartbeat.
        await Runtime.StopAsync(ct);
        Assert.Equal(WorkerStatusCode.Stopped, (await ReadWorkerAsync(ns, ct)).Status);

        await hostCts.CancelAsync();
        await run.WaitAsync(Timeout, ct);
    }

    private async Task WaitForInFlightZeroAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (Runtime.InFlightCount > 0 && sw.Elapsed < Timeout)
        {
            await Task.Delay(20, ct);
        }
        Assert.Equal(0, Runtime.InFlightCount);
    }

    private async Task<JobWorker> ReadWorkerAsync(short namespaceId, CancellationToken ct)
    {
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == namespaceId).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        return worker!;
    }

    private async Task WaitForStatusAsync(long jobId, JobStatusCode status, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Timeout)
        {
            if ((await ReadJobAsync(jobId, ct)).Status == status)
            {
                return;
            }
            await Task.Delay(20, ct);
        }

        // One last read so a timeout fails with the actual status rather than a bare timeout.
        Assert.Equal(status, (await ReadJobAsync(jobId, ct)).Status);
    }
}

/// <summary>Graceful drain under the Buffered (channel-dispatch) profile.</summary>
[ConformanceSpec(
    "runtime.worker-drain.standard",
    "Buffered drain finishes the in-flight job, then Active to Draining to Stopped",
    Area = "Recovery",
    Contract = "Under the Buffered profile a graceful stop flips the worker Active to Draining, runs the in-flight handler to completion, then stamps Stopped.",
    Arrange = "A worker runs the Buffered profile with a gate handler that holds its job in-flight until released.",
    Act = "With the handler in-flight, BeginDrain is called, the gate is released, and the worker is stopped.",
    Assert = "The worker walks Active to Draining to Stopped and the in-flight job finishes Succeeded rather than being cancelled."
)]
public abstract class BufferedWorkerDrainSpec<TFixture> : WorkerDrainSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    protected override ExecutionProfile Profile => ExecutionProfile.Buffered;

    [Fact(
        DisplayName = "Buffered: a graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped"
    )]
    public Task Drain_finishes_in_flight_then_stops() => DrainFinishesInFlightThenStopsAsync(TestContext.Current.CancellationToken);
}

/// <summary>Graceful drain under the Direct (combined claim-execute) profile.</summary>
[ConformanceSpec(
    "runtime.worker-drain.fast",
    "Direct drain finishes the in-flight job, then Active to Draining to Stopped",
    Area = "Recovery",
    Contract = "Under the Direct profile a graceful stop flips the worker Active to Draining, runs the in-flight handler to completion, then stamps Stopped.",
    Arrange = "A worker runs the Direct profile with a gate handler that holds its job in-flight until released.",
    Act = "With the handler in-flight, BeginDrain is called, the gate is released, and the worker is stopped.",
    Assert = "The worker walks Active to Draining to Stopped and the in-flight job finishes Succeeded rather than being cancelled."
)]
public abstract class DirectWorkerDrainSpec<TFixture> : WorkerDrainSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    protected override ExecutionProfile Profile => ExecutionProfile.Direct;

    [Fact(DisplayName = "Direct: a graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped")]
    public Task Drain_finishes_in_flight_then_stops() => DrainFinishesInFlightThenStopsAsync(TestContext.Current.CancellationToken);
}

/// <summary>Graceful drain under the Bulk (group-commit) profile.</summary>
[ConformanceSpec(
    "runtime.worker-drain.bulk",
    "Bulk drain finishes the in-flight job, then Active to Draining to Stopped",
    Area = "Recovery",
    Contract = "Under the Bulk profile a graceful stop flips the worker Active to Draining, runs the in-flight handler to completion and group-commits it, then stamps Stopped.",
    Arrange = "A worker runs the Bulk profile with a one-row completion batch and a gate handler that holds its job in-flight until released.",
    Act = "With the handler in-flight, BeginDrain is called, the gate is released, and the worker is stopped.",
    Assert = "The worker walks Active to Draining to Stopped and the flusher group-commits the in-flight job Succeeded rather than cancelling it."
)]
public abstract class BulkWorkerDrainSpec<TFixture> : WorkerDrainSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    protected override ExecutionProfile Profile => ExecutionProfile.Bulk;

    [Fact(DisplayName = "Bulk: a graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped")]
    public Task Drain_finishes_in_flight_then_stops() => DrainFinishesInFlightThenStopsAsync(TestContext.Current.CancellationToken);
}
