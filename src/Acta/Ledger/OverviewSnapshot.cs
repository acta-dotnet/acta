namespace Acta;

/// <summary>
/// One-shot health counters for the dashboard overview, computed on the database clock.
/// </summary>
/// <param name="ReadyCount">Jobs awaiting a worker: all Ready, plus Suspended jobs whose durable wait has expired.</param>
/// <param name="OldestReadyAgeSeconds">Seconds the oldest such job has been claimable; null when none are due.</param>
/// <param name="ExecutingCount">Jobs currently Executing.</param>
/// <param name="FailedCount">Jobs currently Failed.</param>
/// <param name="UnresolvedAlertCount">Alerts without a resolution timestamp.</param>
/// <param name="UnresolvedCriticalAlertCount">Unresolved alerts at Critical severity.</param>
/// <param name="DeadWorkerCount">Workers marked Dead.</param>
/// <param name="StaleWorkerCount">Active or Draining workers not seen within the stale threshold.</param>
/// <param name="DueSoonScheduleCount">Live schedules due inside the due-soon window.</param>
/// <param name="JobCount">All jobs in scope, including system jobs.</param>
/// <param name="SystemJobCount">System jobs in scope (reserved <c>sys.</c>-prefixed names); JobCount minus this is the user total.</param>
/// <param name="ExecutorCapacity">Executor slots across live workers (Active or Draining, seen inside the stale threshold); what ExecutingCount saturates against.</param>
/// <param name="ScheduleLagSeconds">Seconds the most overdue live schedule is past its next run; null when none are overdue.</param>
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
