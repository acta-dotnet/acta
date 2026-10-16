namespace Acta;

/// <summary>
/// One-shot health counters for the dashboard overview, computed on the database clock: ready jobs (all Ready plus
/// Suspended jobs whose durable wait has expired) and the oldest one's claimable age, executing and failed jobs,
/// unresolved alerts and the Critical ones among them, dead workers and stale ones (Active or Draining, not seen
/// within the stale threshold), schedules due inside the due-soon window and the most overdue one's lag, and the
/// executor capacity across live workers, which <c>ExecutingCount</c> can exceed: a finished job stays Executing
/// until its batched completion write commits.
/// </summary>
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
