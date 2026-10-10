using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// The worker's one claim loop, for both profiles: it takes permits, claims that many rows, and hands each to the
/// executors through the registry's <c>Claimed</c> channel, which holds every permit so the hand-off never blocks. A
/// claimed <c>sys.recovery</c> slot runs on its own task without a permit, so a worker whose executors are all held
/// still sweeps. An empty claim sleeps until the claim horizon, cut short by a wakeup.
/// </summary>
internal sealed class JobClaimer(
    Execution execution,
    JobExecutor executor,
    JobsOptions options,
    WorkerContext context,
    InFlightJobRegistry registry,
    IWorkerWakeup wakeup,
    ILogger log,
    ActaMetrics? metrics = null
)
{
    private readonly TimeSpan _leaseRunway = InFlightJob.LeaseRunway(options);

    // The recovery slots this loop claimed, chained so one runs at a time; awaited when the loop ends.
    private Task _recovery = Task.CompletedTask;

    /// <summary>
    /// Claims until <paramref name="drainCt"/> or <paramref name="hostCt"/> ends a wait. The claim itself runs on
    /// <paramref name="hostCt"/>, so a drain never abandons the rows a committed claim took: they are handed off before
    /// the loop ends and completes the channel.
    /// </summary>
    public async Task RunAsync(CancellationToken hostCt, CancellationToken drainCt)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(hostCt, drainCt);
        try
        {
            var ns = context.WorkerRegistration!.NamespaceName;
            var (namespaceId, workerId) = context.ResolveWorker(ns);
            var direct = options.ExecutionProfile == ExecutionProfile.Direct;
            var minimum = direct ? 1 : options.ClaimBatchSize;
            log.LogInformation(
                "WorkerRuntime: starting claim/dispatch loop with {Count} executors, safety poll {DurationMs}ms ({Detail}).",
                options.MaxConcurrentExecutors,
                (long)options.SafetyPollInterval.TotalMilliseconds,
                $"claim batch {options.ClaimBatchSize}, poll floor {(long)options.MinPollFloor.TotalMilliseconds}ms"
            );
            while (!stop.IsCancellationRequested)
            {
                var permits = 0;
                ClaimBatchResponse result;
                try
                {
                    for (; permits < minimum; permits++)
                    {
                        await registry.Permits.WaitAsync(stop.Token);
                    }
                    while (permits < options.ClaimBatchSize && registry.Permits.Wait(0))
                    {
                        permits++;
                    }

                    var claimedAt = Stopwatch.GetTimestamp();
                    result = await execution.ClaimBatchAsync(
                        new ClaimOptions(
                            namespaceId,
                            workerId,
                            MaxBatch: permits,
                            StartExecuting: direct,
                            ExcludedDefinitionIds: context.UnsupportedDefinitionIdsSnapshot
                        ),
                        options.LeaseTtlSeconds,
                        hostCt
                    );
                    var runway = _leaseRunway - Stopwatch.GetElapsedTime(claimedAt);
                    context.RecoverySlotJobIdByNamespace.TryGetValue(namespaceId, out var slotJobId);
                    foreach (var claimed in result.Jobs)
                    {
                        metrics?.RecordClaim(ns, "claimed");
                        var slot = claimed.JobId == slotJobId;
                        var entry = new InFlightJob(claimed, runway, hostCt, started: direct, holdsPermit: !slot);
                        registry.TryAdd(entry);
                        if (slot)
                        {
                            RunRecovery(entry, hostCt);
                        }
                        else
                        {
                            permits--;
                            registry.Claimed.Writer.TryWrite(entry);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    // A failed claim backs off a full safety interval, not the anti-spin floor, which is for deadline
                    // races: hammering a down database at the floor would log errors many times a second.
                    log.LogError(
                        ex,
                        "WorkerRuntime: claim iteration failed; backing off {DurationMs}ms before retry.",
                        (long)options.SafetyPollInterval.TotalMilliseconds
                    );
                    await Task.Delay(options.SafetyPollInterval, stop.Token);
                    continue;
                }
                finally
                {
                    if (permits > 0)
                    {
                        registry.Permits.Release(permits);
                    }
                }

                if (result.Jobs.Count == 0)
                {
                    metrics?.RecordClaim(ns, "nothing-claimed");
                    var sleep = ComputeSleep(result.Horizon, options.SafetyPollInterval, options.MinPollFloor, options.ClaimIdleJitterMax);
                    var wait = await wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace(ns), sleep, stop.Token);
                    metrics?.RecordWakeupWait(ns, WorkerWakeupPublisher.WaitResultTag(wait));
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // A drain or a stop ended a wait between claims.
        }
        finally
        {
            registry.Claimed.Writer.TryComplete();
            await _recovery;
        }
    }

    /// <summary>
    /// The idle sleep: how long an empty-handed claim loop waits before the next claim, bounded
    /// below by the anti-spin floor and above by the safety poll. Both horizon instants are
    /// DB-sourced, so their difference is a valid duration with no host-clock assumption. Jitter
    /// staggers N workers holding the same deadline; the cap is applied AFTER jitter so
    /// SafetyPollInterval remains the hard upper bound on idle sleep.
    /// </summary>
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

    private void RunRecovery(InFlightJob slot, CancellationToken hostCt)
    {
        var previous = _recovery;
        _recovery = Task.Run(
            async () =>
            {
                await previous;
                try
                {
                    var outcome = await executor.ExecuteClaimedJobAsync(slot, hostCt);
                    log.LogInformation(
                        "WorkerRuntime: ({Namespace}) job {JobId} -> ({Outcome})",
                        context.WorkerRegistration!.NamespaceName,
                        slot.Claim.JobId,
                        outcome.ToString()
                    );
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !hostCt.IsCancellationRequested)
                {
                    log.LogError(ex, "WorkerRuntime: executor faulted on job {JobId}.", slot.Claim.JobId);
                }
                catch (OperationCanceledException)
                {
                    // A hard stop: the slot lapses to recovery like any other claim, and the chain stays awaitable.
                }
            },
            CancellationToken.None
        );
    }
}
