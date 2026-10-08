namespace Acta;

/// <summary>
/// One-shot health counters for the dashboard overview, computed on the database clock: ready jobs (all Ready plus
/// Suspended jobs whose durable wait has expired) and the oldest one's claimable age, executing and failed jobs,
/// unresolved alerts and the Critical ones among them, dead workers and stale ones (Active or Draining, not seen
/// within the stale threshold), schedules due inside the due-soon window and the most overdue one's lag, all jobs in
/// scope and the <c>sys.</c>-prefixed system jobs among them, and the executor capacity across live workers that
/// <c>ExecutingCount</c> saturates against.
/// </summary>
public sealed record OverviewSnapshot(
    long ReadyCount,
    long? OldestReadyAgeSeconds,
    long ExecutingCount,
    long FailedCount,
    long UnresolvedAlertCount,
    long UnresolvedCriticalAlertCount,
    long DeadWorkerCount,
    long StaleWorkerCount,
    long DueSoonScheduleCount,
    long JobCount,
    long SystemJobCount,
    long ExecutorCapacity,
    long? ScheduleLagSeconds
);
