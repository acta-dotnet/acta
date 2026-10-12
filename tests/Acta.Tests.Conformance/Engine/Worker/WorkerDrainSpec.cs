using System.Diagnostics;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The graceful-drain contract: a stop flips the worker
/// Active -&gt; Draining (via the heartbeat, no dedicated routine), keeps the in-flight handler running under
/// the live host token until it finishes, then stamps Stopped. The distinguishing guarantee versus a hard
/// stop is that the in-flight job lands Succeeded - it is NOT cancelled and left for reclaim.
/// </summary>
/// <remarks>
/// Drives the runtime directly: <see cref="WorkerRuntime.RunAsync"/> runs the claim loop and heartbeat,
/// <see cref="WorkerRuntime.BeginDrainAsync"/> begins the drain, and a gate handler holds the job in-flight
/// until the test releases it so the Draining window is observable.
/// </remarks>
[ConformanceSpec(
    "runtime.worker-drain",
    "A drain finishes the in-flight job, then Active to Draining to Stopped",
    Area = "Recovery",
    Contract = "A graceful stop flips the worker Active to Draining, runs the in-flight handler to completion, then stamps Stopped."
)]
public sealed class WorkerDrainSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // A hang guard, not a measurement: these specs drive the real claim loop and its DB round-trips,
    // which run slow under the suite's cross-test parallelism and finish well under a second in
    // isolation. Nothing here asserts on how long the drain took. See SpecWaits.
    private static readonly TimeSpan Timeout = SpecWaits.Gate;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            // The real mechanism this used to paper over: WorkerHeartbeat.TickAsync built its "live" set
            // from the extend-leases queries, then judged whatever was in flight by the time those queries
            // returned - so a job claimed and dispatched mid-tick, after the extend query had already
            // started, was invisible to that tick's live set and got wrongly cancelled (Executing ->
            // Failed). Fixed in WorkerHeartbeat by judging only a snapshot of the in-flight registry taken
            // before the extend queries run. A long lease + dead-after window is kept
            // here regardless, to absorb unrelated timing noise from the suite's aggressive cross-test
            // parallelism (a starved heartbeat tick could otherwise let a lease lapse and be reclaimed).
            // Test-only; production leases stay short. Invariants: LeaseTtl >= 2x heartbeat, WorkerDeadAfter
            // >= 3x heartbeat and > LeaseTtl.
            o.HeartbeatInterval = TimeSpan.FromMilliseconds(500);
            o.LeaseTtlSeconds = 120;
            o.WorkerDeadAfter = TimeSpan.FromSeconds(300);
        });
    }

    private async Task DrainFinishesInFlightThenStopsAsync(CancellationToken ct)
    {
        DrainGate.Reset(TestNamespace);
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "drain-gate", JobPayload.None), ct);

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
        // Poll, not an instant assert: the finalizer writes Succeeded to the DB just before the
        // entry leaves the in-flight registry, so the count can lag the committed Succeeded read by a tick.
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

    private async Task<WorkerEntity> ReadWorkerAsync(int namespaceId, CancellationToken ct)
    {
        var worker = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == namespaceId).SingleOrDefaultAsync(ct);
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

    [Fact(DisplayName = "A graceful stop drains the in-flight job to Succeeded and walks the worker Active -> Draining -> Stopped")]
    public Task Drain_finishes_in_flight_then_stops() => DrainFinishesInFlightThenStopsAsync(TestContext.Current.CancellationToken);
}
