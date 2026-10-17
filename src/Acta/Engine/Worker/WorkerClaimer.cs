using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// The worker's one claim loop: it takes a permit for every free executor, claims that many rows straight to Executing,
/// and hands each to the executors through the registry's <c>Claimed</c> channel, which holds every permit so the
/// hand-off never blocks. An empty claim sleeps until the claim horizon, cut short by a wakeup. While claims come
/// back full, each starts at the previous one's watermark instead of each band's head, and at least once a second
/// from the head again (docs/sql-execution-policy.md, "Claim watermark").
/// </summary>
internal sealed class WorkerClaimer(
    ExecutionOperations execution,
    JobsOptions options,
    WorkerContext context,
    InFlightJobRegistry registry,
    IWorkerWakeup wakeup,
    ILogger log,
    ActaMetrics? metrics = null
)
{
    private readonly TimeSpan _leaseRunway = InFlightJob.LeaseRunway(options);

    // A claim takes a row for every free executor.
    private readonly int _maxClaim = options.MaxConcurrentExecutors;

    // A row stamped due before a concurrent claim but committed after it lands behind that claim's watermark; the
    // margin keeps such rows in reach, and the head claim each second reaches the rest.
    private static readonly TimeSpan WatermarkMargin = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HeadClaimInterval = TimeSpan.FromSeconds(1);

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
            var handedOff = false;
            DateTime? claimFrom = null;
            var headClaimedAt = Stopwatch.GetTimestamp();
            log.LogInformation(
                "WorkerRuntime: starting claim/dispatch loop with {Count} executors, safety poll {DurationMs}ms ({Detail}).",
                options.MaxConcurrentExecutors,
                (long)options.SafetyPollInterval.TotalMilliseconds,
                $"claim up to {_maxClaim}, poll floor {(long)options.MinPollFloor.TotalMilliseconds}ms"
            );
            while (!stop.IsCancellationRequested)
            {
                var permits = 0;
                ClaimJobsResponse result;
                try
                {
                    await registry.Permits.WaitAsync(stop.Token);
                    permits = await TakeFreePermitsAsync(1, settle: handedOff, stop.Token);

                    var claimedAt = Stopwatch.GetTimestamp();
                    if (claimFrom is null || Stopwatch.GetElapsedTime(headClaimedAt, claimedAt) >= HeadClaimInterval)
                    {
                        claimFrom = null;
                        headClaimedAt = claimedAt;
                    }
                    result = await execution.ClaimJobsAsync(
                        new ClaimOptions(
                            namespaceId,
                            workerId,
                            MaxBatch: permits,
                            ExcludedDefinitionIds: context.UnsupportedDefinitionIdsSnapshot,
                            ClaimFromUtc: claimFrom
                        ),
                        options.LeaseTtlSeconds,
                        hostCt
                    );
                    claimFrom = NextClaimFrom(result.Jobs, permits);
                    var runway = _leaseRunway - Stopwatch.GetElapsedTime(claimedAt);
                    handedOff = result.Jobs.Count > 0;
                    foreach (var claimed in result.Jobs)
                    {
                        var entry = new InFlightJob(claimed, runway, hostCt, holdsPermit: true);
                        registry.TryAdd(entry);
                        permits--;
                        registry.Claimed.Writer.TryWrite(entry);
                    }

                    // Counted once every row is handed off: a listener that throws must not strand a claimed row.
                    if (handedOff)
                    {
                        metrics?.RecordClaim(ns, "claimed", result.Jobs.Count);
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
        }
    }

    /// <summary>
    /// Adds every permit free now to <paramref name="permits"/>. With <paramref name="settle"/>, after a claim that
    /// handed rows off, it first lets the executors take them and yields while their permits keep coming back, so
    /// the next claim asks for every executor a quick job frees rather than the first one or two: each claim is a round
    /// trip and a commit on the drain's one serial path. A yield that frees nothing ends the wait, so long handlers
    /// cost it one yield.
    /// </summary>
    private async Task<int> TakeFreePermitsAsync(int permits, bool settle, CancellationToken ct)
    {
        while (settle && registry.Claimed.Reader.Count > 0 && !ct.IsCancellationRequested)
        {
            await Task.Yield();
        }

        int before;
        do
        {
            before = permits;
            if (settle)
            {
                await Task.Yield();
            }
            // Never cancelled mid-count: a permit taken here and lost to a throw would never be released.
            while (permits < _maxClaim && registry.Permits.Wait(0, CancellationToken.None))
            {
                permits++;
            }
        } while (settle && permits > before && permits < _maxClaim);
        return permits;
    }

    /// <summary>
    /// Where the next claim starts: a full claim's latest due instant less the margin. A short claim means the queue
    /// is caught up, so the next claim seeks each band from its head and a late commit waits for nothing.
    /// </summary>
    internal static DateTime? NextClaimFrom(IReadOnlyList<ClaimedJob> claimed, int asked)
    {
        if (claimed.Count < asked)
        {
            return null;
        }

        DateTime? latest = null;
        foreach (var job in claimed)
        {
            if (job.NextRunAtUtc is { } due && (latest is null || due > latest))
            {
                latest = due;
            }
        }
        return latest - WatermarkMargin;
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
}
