using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// <c>sys.retention</c>, the system retention sweep. A recurring, competitively-claimed
/// <c>[Job]</c> auto-registered into every worker namespace; whichever live worker claims the hourly
/// tick runs one namespace-scoped <c>purge_expired_data</c> pass (sections and bounds:
/// <see cref="RetentionSection"/>, <see cref="RetentionSweep"/>). Leaderless: the recurring slot's single claim per tick is the
/// only coordination, so exactly one pass runs per namespace per tick. <c>AuditLevel.Failures</c>
/// keeps idle ticks out of <c>events</c>: a purge pass emits no per-row events.
/// </summary>
internal sealed class RetentionJob(
    MaintenanceOperations maintenance,
    IClockOperations clock,
    IOptions<JobsOptions> options,
    ILogger<RetentionJob>? log = null
)
{
    /// <summary>
    /// Per-tick bound: at most BatchSize * MaxIterations rows deleted per section; the next hourly
    /// fire continues any backlog rather than letting one tick run unbounded.
    /// </summary>
    private const int BatchSize = 1000;
    private const int MaxIterations = 50;

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
        Priority = JobPriorityCode.Critical,
        AuditLevel = DefinitionAuditLevelCode.Failures,
        AlertProfile = AlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.Hourly)]
    public async Task Handle(JobContext ctx, CancellationToken ct)
    {
        await PurgeExpiredDataAsync(
            new RetentionSweep(
                ctx.NamespaceId,
                _eventsRetentionDays,
                _alertRetentionDays,
                _workerRetentionSeconds,
                BatchSize,
                MaxIterations
            ),
            count => LogUndeliveredPurge(ctx, count),
            ct
        );
    }

    public Task<RetentionSweepCounts> PurgeExpiredDataAsync(RetentionSweep command, CancellationToken ct) =>
        PurgeExpiredDataAsync(command, null, ct);

    /// <summary>
    /// Runs bounded atomic batches against cutoffs captured once from the database clock. Reports committed
    /// undelivered-alert deletions once, including when the sweep fails or is cancelled.
    /// </summary>
    public async Task<RetentionSweepCounts> PurgeExpiredDataAsync(
        RetentionSweep command,
        Action<int>? reportUndeliveredPurge,
        CancellationToken ct
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegative(command.MaxIterations);
        var now = await clock.GetUtcNowAsync(ct);
        var counts = new int[8];
        try
        {
            for (var index = 0; index < counts.Length; index++)
            {
                var section = (RetentionSection)(index + 1);
                var cutoff = section switch
                {
                    RetentionSection.Events => now.AddDays(-command.EventsRetentionDays),
                    RetentionSection.SettledAlerts or RetentionSection.UndeliveredAlerts or RetentionSection.PoisonSkipCheckpoints =>
                        now.AddDays(-command.AlertRetention),
                    RetentionSection.Workers => now.AddSeconds(-command.WorkerRetentionSeconds),
                    _ => now,
                };
                var batch = new PurgeExpiredDataCommand(command.NamespaceId, section, cutoff, command.BatchSize);
                for (var iteration = 0; iteration < command.MaxIterations; iteration++)
                {
                    ct.ThrowIfCancellationRequested();
                    var deleted = (await maintenance.PurgeExpiredDataAsync(batch, ct)).Purged;
                    counts[index] = checked(counts[index] + deleted);
                    if (deleted == 0)
                    {
                        break;
                    }
                }
            }

            return new RetentionSweepCounts(counts[0], counts[1], counts[2], counts[3], counts[5], counts[6], counts[7]);
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
    /// One line per pass, never one per row: a namespace whose channel has been down for a quarter
    /// would otherwise log a page of them. Warning, because an alert deleted before it ever reached
    /// a channel is a signal an operator never got - and never an Acta alert of its own, which
    /// would recurse.
    /// </summary>
    private void LogUndeliveredPurge(JobContext ctx, int count) =>
        _log.LogWarning(
            "ACTA sys.retention: purged {Count} alerts in namespace ({Namespace}) that aged out before delivery settled; reason ({Reason}).",
            count,
            ctx.JobNamespace,
            "alert-retention-cap"
        );
}
