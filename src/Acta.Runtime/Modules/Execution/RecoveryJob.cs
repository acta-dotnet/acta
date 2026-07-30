using Acta.Configuration;
using Acta.Modules.Execution;
using Acta.Modules.Execution.ChildLatches;
using Acta.Modules.Execution.Signals;
using Acta.Modules.Execution.Workers;
using Microsoft.Extensions.Options;

namespace Acta.Modules.Execution;

/// <summary>
/// <c>sys.recovery</c>, the system recovery job. A recurring, competitively-claimed <c>[Job]</c>
/// auto-registered into every worker namespace; whichever live worker claims a tick runs one
/// recovery pass. Leaderless: the recurring slot's single claim per tick is the only coordination,
/// so exactly one pass runs per namespace per tick regardless of how many workers are live.
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
internal sealed class RecoveryJob(
    ISignalStore signals,
    IWorkerStore workers,
    IExecutionStore execution,
    IOptions<JobsOptions> options,
    WorkerWakeupPublisher wakeupPublisher
)
{
    private readonly int _deadAfterSeconds = (int)options.Value.WorkerDeadAfter.TotalSeconds;

    /// <summary>
    /// Runs one recovery pass: sweeps dead workers globally (all namespaces), then reclaims stuck
    /// jobs and raises stale child latches for the firing namespace. <c>AuditLevel.Failures</c> keeps
    /// idle ticks out of <c>events</c>: reclaim emits per-job orphaned <c>job.execution.finished</c>
    /// events on affected jobs, so a quiet pass writes nothing. A pass that reclaimed jobs publishes a
    /// wakeup: the reclaimed rows are Ready and their original worker is gone, so a live worker should
    /// pick them up without waiting out the safety poll.
    /// </summary>
    [Job(
        "sys.recovery",
        Priority = JobPriorityCode.Critical,
        AuditLevel = JobAuditLevelCode.Failures,
        AlertProfile = JobAlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.EveryMinute)]
    public async Task Handle(JobContext ctx, CancellationToken ct)
    {
        await workers.MarkDeadWorkersAsync(_deadAfterSeconds, ct);
        var result = await execution.ReclaimStuckJobsAsync(ctx.NamespaceId, ct);
        if (result.Reclaimed > 0)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(ctx.JobNamespace), WorkerWakeupReason.WorkAvailable, ct);
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
        foreach (var latch in await execution.GetStaleChildLatchesAsync(ctx.NamespaceId, ct))
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
    }
}
