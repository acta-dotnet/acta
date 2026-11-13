using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// <c>sys.retention</c>, auto-registered into every worker namespace: whichever live worker claims the hourly tick runs
/// one namespace-scoped <c>purge_expired_data</c> pass, plus the sys namespace's expired events. The slot's single claim
/// per tick is the only coordination. A pass that spends a section's delete budget, or deletes jobs and leaves its walk
/// unfinished, re-arms at once, so a backlog is swept back to back. The pass never runs ahead of the namespace's alert projector: what the projector still owes,
/// events and their jobs, waits for it. <c>Failures</c> recording keeps idle ticks out of <c>events</c>.
/// </summary>
internal sealed class RetentionJob(
    MaintenanceOperations maintenance,
    IClockOperations clock,
    IOptions<JobsOptions> options,
    ILogger<RetentionJob>? log = null
)
{
    /// <summary>
    /// Per-pass bound: at most BatchSize * MaxIterations rows deleted per section, so one attempt stays well inside its
    /// execution timeout; a pass that reaches it runs again at once. A batch stays small because a purged job takes its
    /// steps and checkpoints with it, one delete per table, and SQL Server escalates at 5,000 row locks.
    /// </summary>
    private const int BatchSize = 100;
    private const int MaxIterations = 500;

    /// <summary>
    /// How long one pass walks jobs: a namespace whose definitions keep jobs for different lengths reads the longer-kept
    /// ones on its way to the expired, and the rest of that walk waits for the next pass.
    /// </summary>
    private static readonly TimeSpan JobsWalkBudget = TimeSpan.FromSeconds(30);

    /// <summary>The seeded sys namespace, which owns the tenant events and where no worker runs a pass of its own.</summary>
    /// <summary>
    /// These casts truncate nothing: JobsOptionsValidator already rejected any events or alerts
    /// window that is not a whole number of days. Without that rule a 47-hour alert window would
    /// have purged at 24 hours, deleting a day earlier than the deployment asked for.
    /// </summary>
    private readonly int _eventsRetentionDays = (int)options.Value.JobEventsRetention.TotalDays;
    private readonly int _alertRetentionDays = (int)options.Value.AlertRetention.TotalDays;

    /// <summary>
    /// Ceiling, because retention is destructive: flooring a fractional window would delete up to
    /// a second before the configured instant. The day-granularity windows above are exact by the
    /// validator's whole-days rule; WorkerRetention is exempt from it, so it rounds up here instead.
    /// </summary>
    private readonly int _workerRetentionSeconds = (int)Math.Ceiling(options.Value.WorkerRetention.TotalSeconds);
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
        var swept = await PurgeExpiredDataAsync(
            new RetentionSweep(
                ctx.NamespaceId,
                _eventsRetentionDays,
                _alertRetentionDays,
                _workerRetentionSeconds,
                BatchSize,
                MaxIterations,
                JobsWalkBudget,
                await ctx.GetVariableOrDefaultAsync(RetentionCursor.VariableName, RetentionCursor.None, ct) is { Ticks: > 0 } stored
                    ? stored
                    : null,
                SweepSystemEvents: true
            ),
            count => LogUndeliveredPurge(ctx, count),
            ct,
            place => ctx.SetVariableAsync(RetentionCursor.VariableName, place ?? RetentionCursor.None, ct)
        );
        if (swept.BudgetSpent)
        {
            await ctx.RescheduleAsync(TimeSpan.Zero, "More rows have expired than one pass deletes.", ct);
        }
    }

    /// <summary>
    /// Runs bounded atomic batches against cutoffs captured once from the database clock. Reports committed
    /// undelivered-alert deletions once, including when the sweep fails or is cancelled, and hands the jobs walk's place
    /// to <paramref name="keepJobsPlace"/> as soon as the walk stops, so a later section that fails does not cost it.
    /// </summary>
    public async Task<RetentionSweepCounts> PurgeExpiredDataAsync(
        RetentionSweep command,
        Action<int>? reportUndeliveredPurge,
        CancellationToken ct,
        Func<RetentionCursor?, Task>? keepJobsPlace = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(command.MaxIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(command.JobsWalkBudget, TimeSpan.Zero);
        var now = await clock.GetUtcNowAsync(ct);
        // Read once, before any batch: the cursor only moves forward, so a floor read early keeps at least as much.
        var alertFloor = await maintenance.GetAlertFloorAsync(command.NamespaceId, ct);
        var counts = new int[7];
        var cursor = command.JobsCursor;
        var budgetSpent = false;
        try
        {
            for (var index = 0; index < counts.Length; index++)
            {
                var section = (RetentionSection)(index + 1);
                var cutoff = section switch
                {
                    RetentionSection.Events => now.AddDays(-command.EventsRetentionDays),
                    RetentionSection.SettledAlerts or RetentionSection.UndeliveredAlerts => now.AddDays(-command.AlertRetention),
                    // A terminal worker is also kept for the events window, so an event's worker_id finds its row while the event lasts.
                    RetentionSection.Workers => now.AddSeconds(
                        -Math.Max(command.WorkerRetentionSeconds, command.EventsRetentionDays * TimeSpan.SecondsPerDay)
                    ),
                    _ => now,
                };
                var (floorUtc, floorEventId) = alertFloor.Floor(cutoff);
                var batch = new PurgeExpiredDataCommand(command.NamespaceId, section, cutoff, command.BatchSize)
                {
                    AlertFloorUtc = floorUtc,
                    AlertFloorEventId = floorEventId,
                };
                if (section == RetentionSection.Jobs)
                {
                    // A slice with nothing expired is no reason to stop: the walk ends with its cycle, at the namespace's
                    // oldest job, once a delete budget's worth of jobs is gone, or when its time is spent.
                    var deleteBudget = (long)command.MaxIterations * command.BatchSize;
                    var walking = Stopwatch.StartNew();
                    while (counts[index] < deleteBudget)
                    {
                        ct.ThrowIfCancellationRequested();
                        var walked = await maintenance.PurgeExpiredDataAsync(batch with { Before = cursor }, ct);
                        counts[index] = checked(counts[index] + walked.Purged);
                        cursor = walked.Next;
                        if (cursor is null || walking.Elapsed >= command.JobsWalkBudget)
                        {
                            break;
                        }
                    }
                    // An unfinished cycle runs on at once only when the pass deleted: one that read nothing but jobs still
                    // kept waits for the next tick, or a namespace of long-kept jobs would never rest.
                    budgetSpent |= cursor is not null && counts[index] > 0;
                    if (keepJobsPlace is not null)
                    {
                        await keepJobsPlace(cursor);
                    }

                    continue;
                }
                budgetSpent |= await PurgeSectionAsync(
                    batch,
                    command.MaxIterations,
                    deleted => counts[index] = checked(counts[index] + deleted),
                    ct
                );
                if (section == RetentionSection.Events && command.SweepSystemEvents)
                {
                    // Every namespace's pass also sweeps sys; concurrent passes skip each other's staged rows.
                    var system = batch with { NamespaceId = NameSyntax.SystemNamespaceId, AlertFloorUtc = null, AlertFloorEventId = null };
                    budgetSpent |= await PurgeSectionAsync(system, command.MaxIterations, static _ => { }, ct);
                }
            }

            return new RetentionSweepCounts(
                counts[0],
                counts[1],
                counts[2],
                counts[3],
                counts[4],
                counts[5],
                counts[6],
                cursor,
                budgetSpent
            );
        }
        finally
        {
            // These batches already committed; a failed sweep cannot recover their count on its next run.
            if (counts[3] > 0)
            {
                reportUndeliveredPurge?.Invoke(counts[3]);
            }
        }
    }

    /// <summary>
    /// Deletes one section's batches until a batch finds nothing or <paramref name="maxIterations"/> have run; answers
    /// whether every iteration found rows, so the section may hold more.
    /// </summary>
    private async Task<bool> PurgeSectionAsync(PurgeExpiredDataCommand batch, int maxIterations, Action<int> count, CancellationToken ct)
    {
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = (await maintenance.PurgeExpiredDataAsync(batch, ct)).Purged;
            count(deleted);
            if (deleted == 0)
            {
                return false;
            }
        }

        return maxIterations > 0;
    }

    /// <summary>
    /// One line per pass, never one per row: a namespace whose channel has been down for a quarter
    /// would otherwise log a page of them. Warning, because an alert deleted before it ever reached
    /// a channel is a signal an operator never got - and never an Acta alert of its own, which
    /// would recurse.
    /// </summary>
    private void LogUndeliveredPurge(JobContext ctx, int count)
    {
        if (_log.IsEnabled(LogLevel.Warning))
        {
            ActaLog.UndeliveredAlertsPurged(_log, $"{count} alerts", ctx.JobNamespace);
        }
    }
}

/// <summary>
/// Validated purge sweep bounds for one namespace, and where the jobs walk stopped last time: the walk reads the jobs
/// before <see cref="JobsCursor"/>, and null starts a cycle. <see cref="JobsWalkBudget"/> bounds the time a
/// sweep walks, apart from the rows it deletes: a walk may read jobs still kept before its first delete, and a walk
/// budget tied to the delete budget would take days to pass them on a large table.
/// <see cref="SweepSystemEvents"/> also sweeps the sys namespace's expired events, uncounted.
/// </summary>
internal sealed record RetentionSweep(
    int NamespaceId,
    int EventsRetentionDays,
    int AlertRetention,
    int WorkerRetentionSeconds,
    int BatchSize,
    int MaxIterations,
    TimeSpan JobsWalkBudget,
    RetentionCursor? JobsCursor = null,
    bool SweepSystemEvents = false
);

/// <summary>
/// Per-section deleted counts from one <c>purge_expired_data</c> sweep.
/// <see cref="UndeliveredAlertsPurged"/> is counted apart from <see cref="Alerts"/> because an alert
/// aged out before delivery settled is a lost operator signal, not routine housekeeping. <see cref="JobsCursor"/> is
/// where the jobs walk stopped, for the next sweep to continue from. <see cref="BudgetSpent"/> says a section reached
/// its delete budget while still finding rows, so the next sweep should run at once.
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
