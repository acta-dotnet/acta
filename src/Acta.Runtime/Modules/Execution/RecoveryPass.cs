using Acta.Runtime.Modules.Execution.ChildLatches;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Modules.Execution.Workers;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution;

/// <summary>What one recovery pass changed, for the caller that wants to assert on a single pass.</summary>
internal readonly record struct RecoveryPassOutcome(int DeadWorkersMarked, int ReclaimedJobs, int ReleasedChildLatches);

/// <summary>
/// One recovery pass, and the only implementation of one: the <c>sys.recovery</c> handler and the
/// test host's recovery drive both call this, so neither can model recovery differently from the
/// other.
/// </summary>
/// <remarks>
/// The pass does three things in order. First, <c>mark_dead_workers</c> sweeps <em>all namespaces</em>:
/// it retires <c>workers</c> rows whose process stopped heartbeating past
/// <c>JobsOptions.WorkerDeadAfter</c>. Then <c>reclaim_stuck_jobs</c> and the child-latch backstop
/// operate on the <em>firing namespace only</em>: reclaim recovers jobs whose lease expired past the
/// heartbeat margin, and the latch passes re-raise terminal statuses lost to a crash. Dead workers are
/// swept first; the two signals (worker <c>last_seen</c> vs per-job lease expiry) are independent so
/// order is not otherwise load-bearing.
/// </remarks>
internal sealed class RecoveryPass(
    ISignalStore signals,
    IWorkerStore workers,
    IExecutionStore execution,
    IOptions<JobsOptions> options,
    WorkerWakeupPublisher wakeupPublisher
)
{
    /// <summary>
    /// Ceiling, because the boundary is a promise to the worker: flooring a fractional window (a
    /// 45.1s heartbeat derives 315.7s) would tombstone a live worker up to a second early.
    /// </summary>
    private readonly int _deadAfterSeconds = (int)Math.Ceiling(options.Value.WorkerDeadAfter.TotalSeconds);

    /// <summary>
    /// Sweeps dead workers globally, then reclaims stuck jobs and raises stale child latches for
    /// <paramref name="namespaceId"/>. A pass that reclaimed jobs publishes a wakeup: the reclaimed rows
    /// are claimable - Ready, or Suspended on a deadline already past for the one arm that re-arms a
    /// resolved wait uncharged - and their original worker is gone, so a live worker should pick them up
    /// without waiting out the safety poll.
    /// </summary>
    public async Task<RecoveryPassOutcome> RunAsync(int namespaceId, string namespaceName, CancellationToken ct)
    {
        var deadWorkers = await workers.MarkDeadWorkersAsync(_deadAfterSeconds, ct);
        var result = await execution.ReclaimStuckJobsAsync(namespaceId, ct);
        if (result.Reclaimed > 0)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(namespaceName), WorkerWakeupReason.WorkAvailable, ct);
        }

        // A budget-exhausted child landed Failed with no worker completion to raise its latch.
        var released = 0;
        foreach (var (childId, parentId) in result.FailedChildren)
        {
            if (await RaiseChildLatch.Run(signals, childId, parentId, JobStatusCode.Failed, ct))
            {
                released++;
            }
        }

        // Backstop for raises lost to a crash between a child's terminal landing and its follow-up
        // raise (and for latches re-armed after a state reset): re-raise the child's terminal status.
        foreach (var latch in await execution.GetStaleChildLatchesAsync(namespaceId, ct))
        {
            if (await RaiseChildLatch.Run(signals, latch.ChildJobId, latch.ParentJobId, latch.ChildStatus ?? JobStatusCode.Failed, ct))
            {
                released++;
            }
        }

        // A released parent may live in another namespace; only numeric ids are in scope here, so
        // wake every worker namespace.
        if (released > 0)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return new RecoveryPassOutcome(deadWorkers, result.Reclaimed, released);
    }
}
