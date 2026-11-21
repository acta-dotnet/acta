using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// <c>sys.retention</c>, auto-registered into every worker namespace: whichever live worker claims the hourly tick deletes
/// the namespace's expired jobs, events, alerts, and workers, the expired locks, and the unused lanes, plus the sys
/// namespace's expired events. The slot's single claim per tick is the only coordination. A pass that spends a delete's
/// budget, or deletes jobs and leaves its walk unfinished, re-arms at once, so a backlog is swept back to back. Jobs and
/// events wait for the namespace's alert projector. <c>Failures</c> recording keeps idle ticks out of <c>events</c>.
/// </summary>
internal sealed class RetentionJob(
    MaintenanceOperations maintenance,
    IClockOperations clock,
    IOptions<JobsOptions> options,
    ILogger<RetentionJob>? log = null
)
{
    /// <summary>
    /// Per-pass bound: at most BatchSize * MaxIterations rows deleted per delete, so one attempt stays well inside its
    /// execution timeout; a pass that reaches it runs again at once. A batch stays small because a purged job takes its
    /// steps and checkpoints with it, one delete per table, and SQL Server escalates at 5,000 row locks.
    /// </summary>
    private const int BatchSize = 100;
    private const int MaxIterations = 500;

    /// <summary>
    /// How long one pass walks the expired jobs: the ones a guard keeps, such as the finished children of a parent that
    /// has not finished, stay in the walk's path, and what a pass has no time to read waits for the next.
    /// </summary>
    private static readonly TimeSpan JobsWalkBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// This cast truncates nothing: JobsOptionsValidator already rejected a window that is not a
    /// whole number of days. Without that rule a 47-hour window would have purged at 24 hours,
    /// deleting a day earlier than the deployment asked for.
    /// </summary>
    private readonly int _retentionDays = (int)options.Value.Retention.TotalDays;
    private readonly ILogger _log = log ?? NullLogger<RetentionJob>.Instance;

    [Job(
        "sys.retention",
        Priority = JobPriorityCode.Realtime,
        EventRecording = EventRecordingCode.Failures,
        AlertProfile = AlertProfileCode.SysCritical,
        Backoff = SystemJobs.Backoff
    )]
    [Schedule("default", Cron.Hourly)]
    public async Task Handle(JobContext ctx, CancellationToken ct)
    {
        var swept = await SweepAsync(
            new RetentionSweep(
                ctx.NamespaceId,
                ctx.JobNamespace,
                _retentionDays,
                BatchSize,
                MaxIterations,
                JobsWalkBudget,
                await ctx.GetVariableOrDefaultAsync(RetentionCursor.VariableName, RetentionCursor.None, ct) is { Ticks: > 0 } stored
                    ? stored
                    : null,
                SweepSystemEvents: true
            ),
            ct,
            place => ctx.SetVariableAsync(RetentionCursor.VariableName, place ?? RetentionCursor.None, ct)
        );
        if (swept.BudgetSpent)
        {
            await ctx.RescheduleAsync(TimeSpan.Zero, "More rows have expired than one pass deletes.", ct);
        }
    }

    /// <summary>
    /// Runs the six deletes in bounded atomic batches against cutoffs captured once from the database clock. Warns once
    /// about committed undelivered-alert deletions, including when the sweep fails or is cancelled, and hands the jobs
    /// walk's place to <paramref name="keepJobsPlace"/> as soon as the walk stops, so a later delete that fails does not
    /// cost it.
    /// </summary>
    public async Task<RetentionSweepCounts> SweepAsync(
        RetentionSweep command,
        CancellationToken ct,
        Func<RetentionCursor?, Task>? keepJobsPlace = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(command.MaxIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.JobsWalkBudget, TimeSpan.Zero);
        var now = await clock.GetUtcNowAsync(ct);
        var window = now.AddDays(-command.RetentionDays);
        // Read once, before any batch: the projector only moves forward, so a position read early keeps at least as much.
        var floor = await maintenance.GetAlertFloorAsync(command.NamespaceId, ct);
        var (jobs, events, alerts, undelivered, workers, locks, lanes) = (0, 0, 0, 0, 0, 0, 0);
        var cursor = command.JobsCursor;
        var budgetSpent = false;
        try
        {
            var jobsBatch = new PurgeExpiredJobsCommand(command.NamespaceId, Earlier(now, floor), command.BatchSize);
            // A projector that has read nothing stands at the start of time, before which nothing lies.
            if (jobsBatch.CutoffUtc > DateTime.MinValue)
            {
                // A batch its guards kept whole is no reason to stop: the walk ends at the end of the expired range, once
                // a delete budget's worth of jobs is gone, or when its time is spent.
                var deleteBudget = (long)command.MaxIterations * command.BatchSize;
                var walking = Stopwatch.StartNew();
                while (jobs < deleteBudget)
                {
                    ct.ThrowIfCancellationRequested();
                    var walked = await maintenance.PurgeExpiredJobsAsync(jobsBatch with { After = cursor }, ct);
                    jobs = checked(jobs + walked.Purged);
                    cursor = walked.Next;
                    if (cursor is null || walking.Elapsed >= command.JobsWalkBudget)
                    {
                        break;
                    }
                }
            }
            // An unfinished walk runs on at once only when the pass deleted: one that read nothing but jobs its guards
            // keep waits for the next tick, or a namespace of such jobs would never rest.
            budgetSpent |= cursor is not null && jobs > 0;
            if (keepJobsPlace is not null)
            {
                await keepJobsPlace(cursor);
            }

            budgetSpent |= await PurgeEventsAsync(
                command,
                command.NamespaceId,
                Earlier(window, floor),
                deleted => events = checked(events + deleted),
                ct
            );
            if (command.SweepSystemEvents)
            {
                // Every namespace's pass also sweeps sys, where no projector runs; concurrent passes skip each other's
                // staged rows.
                budgetSpent |= await PurgeEventsAsync(command, NameSyntax.SystemNamespaceId, window, static _ => { }, ct);
            }

            var alertsBatch = new PurgeExpiredAlertsCommand(command.NamespaceId, window, command.BatchSize);
            budgetSpent |= await RepeatAsync(
                command.MaxIterations,
                async () =>
                {
                    var purged = await maintenance.PurgeExpiredAlertsAsync(alertsBatch, ct);
                    undelivered = checked(undelivered + purged.Undelivered);
                    return purged.Purged;
                },
                deleted => alerts = checked(alerts + deleted),
                ct
            );
            var workersBatch = new PurgeExpiredWorkersCommand(command.NamespaceId, window, command.BatchSize);
            budgetSpent |= await RepeatAsync(
                command.MaxIterations,
                async () => (await maintenance.PurgeExpiredWorkersAsync(workersBatch, ct)).Purged,
                deleted => workers = checked(workers + deleted),
                ct
            );
            var locksBatch = new PurgeExpiredLocksCommand(now, command.BatchSize);
            budgetSpent |= await RepeatAsync(
                command.MaxIterations,
                async () => (await maintenance.PurgeExpiredLocksAsync(locksBatch, ct)).Purged,
                deleted => locks = checked(locks + deleted),
                ct
            );
            var lanesBatch = new PurgeUnusedLanesCommand(command.NamespaceId, command.BatchSize);
            budgetSpent |= await RepeatAsync(
                command.MaxIterations,
                async () => (await maintenance.PurgeUnusedLanesAsync(lanesBatch, ct)).Purged,
                deleted => lanes = checked(lanes + deleted),
                ct
            );

            return new RetentionSweepCounts(jobs, events, alerts - undelivered, undelivered, workers, locks, lanes, cursor, budgetSpent);
        }
        finally
        {
            // These batches already committed; a failed sweep cannot recover their count on its next run.
            if (undelivered > 0)
            {
                LogUndeliveredPurge(command.JobNamespace, undelivered);
            }
        }
    }

    /// <summary>
    /// The earlier of <paramref name="cutoffUtc"/> and the instant the namespace's alert projector has read to, so what the
    /// projector has not passed waits for it; the cutoff alone where no projector runs.
    /// </summary>
    private static DateTime Earlier(DateTime cutoffUtc, GetAlertFloorResponse floor) =>
        floor.Floor(cutoffUtc).Utc is var read && read < cutoffUtc ? read : cutoffUtc;

    /// <summary>
    /// Deletes one namespace's events before <paramref name="cutoffUtc"/>, each batch reading on from the last entry the
    /// batch before it examined, so no batch reads again what an earlier one deleted.
    /// </summary>
    private Task<bool> PurgeEventsAsync(
        RetentionSweep command,
        int namespaceId,
        DateTime cutoffUtc,
        Action<int> count,
        CancellationToken ct
    )
    {
        var batch = new PurgeExpiredEventsCommand(namespaceId, cutoffUtc, command.BatchSize, DateTime.MinValue, 0);
        return RepeatAsync(
            command.MaxIterations,
            async () =>
            {
                var purged = await maintenance.PurgeExpiredEventsAsync(batch, ct);
                batch = batch with { AfterCreatedUtc = purged.LastCreatedUtc, AfterEventId = purged.LastEventId };
                return purged.Purged;
            },
            count,
            ct
        );
    }

    /// <summary>
    /// Runs one delete's batches until a batch finds nothing or <paramref name="maxIterations"/> have run, counting each;
    /// answers whether every batch found rows, so the delete may have more.
    /// </summary>
    private static async Task<bool> RepeatAsync(int maxIterations, Func<Task<int>> batch, Action<int> count, CancellationToken ct)
    {
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await batch();
            count(deleted);
            if (deleted == 0)
            {
                return false;
            }
        }

        return maxIterations > 0;
    }

    /// <summary>
    /// One line per pass, never one per row: a namespace whose channel has been down past the window
    /// would otherwise log a page of them. Warning, because an alert deleted before it ever reached
    /// a channel is a signal an operator never got - and never an Acta alert of its own, which
    /// would recurse.
    /// </summary>
    private void LogUndeliveredPurge(string jobNamespace, int count)
    {
        if (_log.IsEnabled(LogLevel.Warning))
        {
            ActaLog.UndeliveredAlertsPurged(_log, $"{count} alerts", jobNamespace);
        }
    }
}

/// <summary>
/// Validated purge sweep bounds for one namespace, and where the jobs walk stopped last time: the walk reads the expired
/// jobs after <see cref="JobsCursor"/>, and null starts at the oldest. <see cref="JobsWalkBudget"/> bounds the time a
/// sweep walks, apart from the rows it deletes: a walk may read jobs its guards keep before its first delete, and a walk
/// budget tied to the delete budget would take days to pass them on a large table.
/// <see cref="SweepSystemEvents"/> also sweeps the sys namespace's expired events, uncounted.
/// </summary>
internal sealed record RetentionSweep(
    int NamespaceId,
    string JobNamespace,
    int RetentionDays,
    int BatchSize,
    int MaxIterations,
    TimeSpan JobsWalkBudget,
    RetentionCursor? JobsCursor = null,
    bool SweepSystemEvents = false
);

/// <summary>
/// Per-delete counts from one <c>sys.retention</c> sweep.
/// <see cref="UndeliveredAlertsPurged"/> is counted apart from the settled <see cref="Alerts"/> because an alert
/// aged out before delivery settled is a lost operator signal, not routine housekeeping. <see cref="JobsCursor"/> is
/// where the jobs walk stopped, for the next sweep to continue from. <see cref="BudgetSpent"/> says a delete reached
/// its budget while still finding rows, so the next sweep should run at once.
/// </summary>
internal readonly record struct RetentionSweepCounts(
    int Jobs,
    int Events,
    int Alerts,
    int UndeliveredAlertsPurged,
    int Workers,
    int Locks,
    int Lanes,
    RetentionCursor? JobsCursor,
    bool BudgetSpent = false
);
