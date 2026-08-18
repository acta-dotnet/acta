using System.Threading.Channels;
using Acta.Runtime.Hosting;
using Acta.Runtime.Kernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// The claim/dispatch loop. A single producer (<see cref="ClaimLoopAsync"/>) claims Ready jobs
/// into a bounded channel; N executor loops (<see cref="ExecutorLoopAsync"/>) drain it and drive each
/// claimed job through the <see cref="JobExecutor"/>. Self-gates on <see cref="WorkerRegistration"/>
/// so enqueue-only deployments never enter the loop.
/// </summary>
internal sealed class WorkerLoop(
    Acta.Runtime.Modules.Execution.IExecutionStore execution,
    JobExecutor executor,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    IWorkerWakeup wakeup,
    ILogger? log = null,
    JobMetrics? metrics = null,
    CompletionSink? completionSink = null
)
{
    private readonly Acta.Runtime.Modules.Execution.IExecutionStore _execution = execution;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly JobExecutor _executor = executor;
    private readonly IOptions<JobsOptions> _options = options;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly IWorkerWakeup _wakeup = wakeup;
    private readonly CompletionSink? _completionSink = completionSink;
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly JobMetrics? _metrics = metrics;

    public Task RunLoopAsync(CancellationToken ct) => RunLoopAsync(ct, ct);

    // hostCt cancels in-flight execution (a hard stop); drainCt only stops the claim producer, so a
    // graceful drain lets in-flight handlers run to completion under the still-live hostCt before the loop
    // returns. A hard stop passes the same token for both; the single-token overload above keeps that
    // shape for tests and callers that don't drain.
    public async Task RunLoopAsync(CancellationToken hostCt, CancellationToken drainCt)
    {
        if (_workerRegistration is null)
        {
            _log.LogInformation("WorkerRuntime: enqueue-only mode; no poll loop.");
            return;
        }

        // Stops the claim producer on either a drain (graceful) or the host stop (hard); executors and the
        // in-flight handlers keep running on hostCt, so a drain finishes their work before the loop ends.
        using var claimStop = CancellationTokenSource.CreateLinkedTokenSource(hostCt, drainCt);
        var ns = _workerRegistration.NamespaceName;
        var (namespaceId, workerId) = _context.ResolveWorker(ns);
        var executorCount = Math.Max(1, _options.Value.MaxConcurrentExecutors);
        var claimBatchSize = Math.Max(1, _options.Value.ClaimBatchSize);

        _log.LogInformation(
            "WorkerRuntime: starting claim/dispatch loop with {Count} executors, safety poll {DurationMs}ms ({Detail}).",
            executorCount,
            (long)_options.Value.SafetyPollInterval.TotalMilliseconds,
            $"claim batch {claimBatchSize}, poll floor {(long)_options.Value.MinPollFloor.TotalMilliseconds}ms"
        );

        var profile = _options.Value.ExecutionProfile;
        if (profile is ExecutionProfile.Direct or ExecutionProfile.Bulk)
        {
            // Bulk is Direct plus a group-commit completion buffer: run the same combined claim-execute loop,
            // but with the flusher draining buffered completions alongside it. On shutdown the dispatch
            // loop drains its in-flight handlers (so every completion is buffered), then CompleteWriter
            // lets the flusher group-commit the remainder.
            if (profile == ExecutionProfile.Bulk && _completionSink is { } sink)
            {
                // Several flushers run in parallel so group commit does not serialize all completions
                // through one connection (that would lose the parallelism Direct gets from N executors).
                var flusherCount = Math.Clamp(executorCount / 4, 1, 16);
                var flusher = sink.RunFlushersAsync(flusherCount);
                try
                {
                    await CombinedLoopAsync(ns, namespaceId, workerId, executorCount, claimBatchSize, hostCt, claimStop.Token);
                }
                finally
                {
                    sink.CompleteWriter();
                    await flusher;
                }
            }
            else
            {
                await CombinedLoopAsync(ns, namespaceId, workerId, executorCount, claimBatchSize, hostCt, claimStop.Token);
            }

            return;
        }

        // Claim feeds this bounded channel while executor loops drain it concurrently. Capacity holds
        // a full claim batch, or the executor count when larger, so a batch can buffer without the
        // producer blocking mid-batch while leases tick. If a buffered claim's lease expires and is
        // reclaimed before an executor reaches it, the runtime version changes and start refuses it
        // as a clean lost-claim skip for the next tick. Over-claiming is wasteful at worst, never
        // double execution.
        var channel = Channel.CreateBounded<ClaimedJob>(
            new BoundedChannelOptions(Math.Max(executorCount, claimBatchSize))
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );

        var executors = new Task[executorCount];
        for (var i = 0; i < executorCount; i++)
        {
            // No cancellation token on Task.Run: a cancelled hostCt must let the executor START and exit
            // cleanly through ReadAllAsync, not leave an unscheduled task Canceled (which Task.WhenAll below
            // would rethrow as a fault). Cancellation is handled inside ExecutorLoopAsync via hostCt.
            executors[i] = Task.Run(() => ExecutorLoopAsync(channel.Reader, ns, namespaceId, workerId, hostCt));
        }

        try
        {
            await ClaimLoopAsync(channel.Writer, ns, namespaceId, workerId, claimStop.Token);
        }
        finally
        {
            // No more work arrives: complete the writer so ReadAllAsync drains then ends. In-flight
            // handlers already observed ct cancel; buffered-but-unstarted claims fall to lease expiry.
            channel.Writer.TryComplete();
            await Task.WhenAll(executors);
        }
    }

    // Producer: claim Ready jobs and hand them off. WriteAsync blocks when the channel is full (all
    // executors busy + buffer full), natural backpressure capping how far ahead the worker claims.
    // An empty claim sleeps until the horizon's nearest run time (capped by the safety poll), and the
    // wakeup transport interrupts that sleep the moment a publish makes work claimable, so idle
    // pickup is signal-latency, not poll-cadence.
    private async Task ClaimLoopAsync(ChannelWriter<ClaimedJob> writer, string ns, short namespaceId, int workerId, CancellationToken ct)
    {
        var options = _options.Value;
        var batchSize = Math.Max(1, options.ClaimBatchSize);
        var safetyPoll = options.SafetyPollInterval;
        var floor = options.MinPollFloor;
        var jitterMax = options.ClaimIdleJitterMax;
        var channel = WorkerWakeupChannel.WorkerNamespace(ns);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _execution.ClaimBatchAsync(
                    new ClaimRequest(namespaceId, workerId, MaxBatch: batchSize),
                    _leaseTtlSeconds,
                    ct
                );
                if (result.Jobs.Count == 0)
                {
                    _metrics?.RecordClaim(ns, "nothing-claimed");
                    var sleep = ComputeSleep(result.Horizon, safetyPoll, floor, jitterMax);
                    var wait = await _wakeup.WaitAsync(channel, sleep, ct);
                    _metrics?.RecordWakeupWait(ns, WorkerWakeupPublisher.WaitResultTag(wait));
                    continue;
                }

                // Hand off every claimed row; WriteAsync applies backpressure if the batch outruns the
                // executors, so a large batch never over-buffers beyond the channel capacity. A
                // non-empty claim re-claims immediately: a backlog keeps the loop hot, and signals
                // published meanwhile coalesce into the next empty-claim wait.
                foreach (var claimed in result.Jobs)
                {
                    _metrics?.RecordClaim(ns, "claimed");
                    await writer.WriteAsync(claimed, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed claim (DB outage, transient fault) backs off a full safety interval; the
                // anti-spin floor is for deadline races, not error retry; hammering a down DB at the
                // floor cadence would spam errors many times a second. A wakeup publish cannot shorten this wait.
                _log.LogError(
                    ex,
                    "WorkerRuntime: claim iteration failed; backing off {DurationMs}ms before retry.",
                    (long)safetyPoll.TotalMilliseconds
                );
                try
                {
                    await Task.Delay(safetyPoll, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Stop/drain landed during the error backoff: exit cleanly rather than faulting the loop.
                    break;
                }
            }
        }
    }

    // The idle sleep: how long an empty-handed claim loop waits before the next claim, bounded below
    // by the anti-spin floor and above by the safety poll. Both horizon instants are DB-sourced, so
    // their difference is a valid duration with no host-clock assumption. Jitter staggers N workers
    // holding the same deadline; the cap is applied AFTER jitter so SafetyPollInterval remains the
    // hard upper bound on idle sleep.
    internal static TimeSpan ComputeSleep(ClaimHorizon? horizon, TimeSpan safetyPoll, TimeSpan floor, TimeSpan jitterMax)
    {
        if (horizon is not { NextReadyAtUtc: { } nextReady } h)
        {
            // No Ready rows at all (or a degenerate empty result); nothing to time against.
            return safetyPoll;
        }

        var untilDue = nextReady - h.DbNowUtc;
        if (untilDue <= TimeSpan.Zero)
        {
            // Due rows exist but were transiently locked away (SKIP-LOCKED race); quick retry.
            return floor;
        }

        var baseDelay = untilDue < floor ? floor : untilDue;
        if (baseDelay >= safetyPoll)
        {
            return safetyPoll;
        }

        var jitterTicks = jitterMax > TimeSpan.Zero ? Random.Shared.NextInt64(jitterMax.Ticks + 1) : 0;
        var jittered = baseDelay + TimeSpan.FromTicks(jitterTicks);
        return jittered > safetyPoll ? safetyPoll : jittered;
    }

    // Consumer: one of N loops draining the shared channel. Each job runs to completion before the
    // loop pulls the next, so live concurrency equals the executor count. A per-job fault is logged
    // and swallowed so one bad job never tears the executor down.
    private async Task ExecutorLoopAsync(ChannelReader<ClaimedJob> reader, string ns, short namespaceId, int workerId, CancellationToken ct)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(ct))
            {
                try
                {
                    var outcome = await _executor.ExecuteClaimedJobAsync(job, ns, namespaceId, workerId, alreadyStarted: false, ct);
                    if (outcome != RunOnceOutcome.NothingClaimed)
                    {
                        _log.LogInformation("WorkerRuntime: {Namespace} job {JobId} -> {Outcome}", ns, job.JobId, outcome);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "WorkerRuntime: executor faulted on job {JobId}.", job.JobId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: ReadAllAsync observed cancellation. Buffered claims fall to lease expiry.
        }
    }

    /// <summary>
    /// The combined claim-execute coordinator (Direct profile). A SemaphoreSlim sizes each claim
    /// to the free-executor count and hands each claimed row straight to a running Task, so there is no
    /// Channel and no buffered-Dispatched window: claim_batch transitions Ready->Executing directly.
    /// </summary>
    private async Task CombinedLoopAsync(
        string ns,
        short namespaceId,
        int workerId,
        int executorCount,
        int batchSize,
        CancellationToken hostCt,
        CancellationToken claimCt
    )
    {
        var options = _options.Value;
        var safetyPoll = options.SafetyPollInterval;
        var floor = options.MinPollFloor;
        var jitterMax = options.ClaimIdleJitterMax;
        var channel = WorkerWakeupChannel.WorkerNamespace(ns);
        var slots = new SemaphoreSlim(executorCount, executorCount);

        try
        {
            // claimCt gates the producer (claim + idle wait); a drain cancels it to stop taking new work,
            // while started jobs keep running on hostCt.
            while (!claimCt.IsCancellationRequested)
            {
                await slots.WaitAsync(claimCt);
                var acquired = 1;
                while (acquired < batchSize && await slots.WaitAsync(0))
                {
                    acquired++;
                }

                ClaimResult result;
                try
                {
                    result = await _execution.ClaimBatchAsync(
                        new ClaimRequest(namespaceId, workerId, MaxBatch: acquired, StartExecuting: true),
                        _leaseTtlSeconds,
                        claimCt
                    );
                }
                catch (OperationCanceledException) when (claimCt.IsCancellationRequested)
                {
                    slots.Release(acquired);
                    break;
                }
                catch (Exception ex)
                {
                    slots.Release(acquired);
                    _log.LogError(
                        ex,
                        "WorkerRuntime: combined claim iteration failed; backing off {DurationMs}ms before retry.",
                        (long)safetyPoll.TotalMilliseconds
                    );
                    await Task.Delay(safetyPoll, claimCt);
                    continue;
                }

                var claimed = result.Jobs;
                if (claimed.Count < acquired)
                {
                    slots.Release(acquired - claimed.Count);
                }

                if (claimed.Count == 0)
                {
                    _metrics?.RecordClaim(ns, "nothing-claimed");
                    var sleep = ComputeSleep(result.Horizon, safetyPoll, floor, jitterMax);
                    var wait = await _wakeup.WaitAsync(channel, sleep, claimCt);
                    _metrics?.RecordWakeupWait(ns, WorkerWakeupPublisher.WaitResultTag(wait));
                    continue;
                }

                // Execution runs on hostCt, never claimCt: a drain stops claiming but lets every started job
                // run to completion (the drain below awaits them); only a hard stop cancels in-flight work.
                foreach (var job in claimed)
                {
                    _metrics?.RecordClaim(ns, "claimed");
                    _ = RunOneAsync(job, ns, namespaceId, workerId, slots, hostCt);
                }
            }
        }
        catch (OperationCanceledException) when (claimCt.IsCancellationRequested)
        {
            // Stop claiming: a slot or wakeup wait observed cancellation (drain or hard stop). The drain
            // below awaits in-flight completion; on a hard stop those already observed hostCt. Unclaimed
            // Ready rows fall to lease expiry.
        }
        finally
        {
            // Drain without tracking a task list: re-acquire every permit. Holding all executorCount permits
            // means every in-flight job released - finished on a drain, unwound on a hard stop.
            for (var i = 0; i < executorCount; i++)
            {
                await slots.WaitAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Runs one already-started job to completion, then releases its slot. A per-job fault is logged and
    /// swallowed so one bad job never tears the coordinator down.
    /// </summary>
    private async Task RunOneAsync(ClaimedJob job, string ns, short namespaceId, int workerId, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            var outcome = await _executor.ExecuteClaimedJobAsync(job, ns, namespaceId, workerId, alreadyStarted: true, ct);
            if (outcome != RunOnceOutcome.NothingClaimed)
            {
                _log.LogInformation("WorkerRuntime: {Namespace} job {JobId} -> {Outcome}", ns, job.JobId, outcome);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "WorkerRuntime: executor faulted on job {JobId}.", job.JobId);
        }
        finally
        {
            slots.Release();
        }
    }
}
