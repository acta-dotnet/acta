namespace Acta;

/// <summary>
/// One-shot health counters for the dashboard overview, computed on the database clock: ready jobs (all Ready plus
/// Suspended jobs whose durable wait has expired) and the oldest one's claimable age, executing and failed jobs,
/// unresolved alerts and the Critical ones among them, dead workers and stale ones (Active or Draining, not seen
/// within the stale threshold), schedules due inside the due-soon window and the most overdue one's lag, and the
/// executor capacity across live workers, which <c>ExecutingCount</c> can exceed: a finished job stays Executing
/// until its batched completion write commits.
/// </summary>
/// <param name="ReadyCount">How many jobs are ready to be claimed.</param>
/// <param name="OldestReadyAgeSeconds">How long the oldest ready job has waited, in seconds; null when none is ready.</param>
/// <param name="ExecutingCount">How many jobs a worker has claimed, including finished ones whose completion is still being
/// written.</param>
/// <param name="FailedCount">How many jobs are in the failed status.</param>
/// <param name="UnresolvedAlertCount">How many alerts are unresolved.</param>
/// <param name="UnresolvedCriticalAlertCount">How many unresolved alerts are critical.</param>
/// <param name="DeadWorkerCount">How many workers are marked dead.</param>
/// <param name="StaleWorkerCount">How many workers have stopped sending heartbeats without being marked dead.</param>
/// <param name="DueSoonScheduleCount">How many schedules have an occurrence due soon.</param>
/// <param name="ExecutorCapacity">How many jobs the live workers can run at once, in total.</param>
/// <param name="ScheduleLagSeconds">How far the most overdue schedule is behind, in seconds; null when none is overdue.</param>
public sealed record GetOverviewResponse(
    long ReadyCount,
    long? OldestReadyAgeSeconds,
    long ExecutingCount,
    long FailedCount,
    long UnresolvedAlertCount,
    long UnresolvedCriticalAlertCount,
    long DeadWorkerCount,
    long StaleWorkerCount,
    long DueSoonScheduleCount,
    long ExecutorCapacity,
    long? ScheduleLagSeconds
);
