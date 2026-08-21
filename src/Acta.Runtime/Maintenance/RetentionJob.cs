using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Maintenance;

/// <summary>
/// <c>sys.retention</c>, the system retention sweep. A recurring, competitively-claimed
/// <c>[Job]</c> auto-registered into every worker namespace; whichever live worker claims the hourly
/// tick runs one namespace-scoped <c>purge_expired_data</c> pass. Leaderless: the recurring slot's
/// single claim per tick is the only coordination, so exactly one pass runs per namespace per tick.
/// </summary>
/// <remarks>
/// One pass deletes, in bounded batches: terminal <c>job</c> rows past their <c>retention_until_utc</c>
/// (cascading the per-job child tables), <c>events</c> rows older than
/// <c>JobsOptions.JobEventsRetention</c>, <c>alerts</c> rows older than
/// <c>JobsOptions.AlertRetention</c> - settled or not, the hard cap - with the projector's aged
/// poison-skip variables, and terminal (Stopped or Dead) <c>workers</c> rows older than
/// <c>JobsOptions.WorkerRetention</c>, then reaps expired <c>leases</c> lock rows globally. The batch
/// and iteration caps bound each tick; the next hourly fire continues any backlog. <c>AuditLevel.Failures</c>
/// keeps idle ticks out of <c>events</c>: a purge pass emits no per-row events.
/// </remarks>
internal sealed class RetentionJob(IRetentionStore store, IOptions<JobsOptions> options, ILogger<RetentionJob>? log = null)
{
    // Per-tick bound: at most BatchSize * MaxIterations rows deleted per section; the next hourly fire
    // continues any backlog rather than letting one tick run unbounded.
    private const int BatchSize = 1000;
    private const int MaxIterations = 50;

    // Exact by construction, not by luck: JobsOptionsValidator rejects an events or alerts window that
    // is not a whole number of days, so these casts truncate nothing. Without that rule a 47-hour alert
    // window would have purged at 24 hours, deleting a day earlier than the deployment asked for.
    private readonly int _eventsRetentionDays = (int)options.Value.JobEventsRetention.TotalDays;
    private readonly int _alertRetentionDays = (int)options.Value.AlertRetention.TotalDays;
    private readonly int _workerRetentionSeconds = (int)options.Value.WorkerRetention.TotalSeconds;
    private readonly ILogger _log = log ?? NullLogger<RetentionJob>.Instance;

    /// <summary>
    /// Runs one retention sweep for the firing namespace: terminal jobs, expired events, alerts past
    /// their window whether delivery settled or not, dead workers.
    /// </summary>
    [Job(
        "sys.retention",
        Priority = JobPriorityCode.Critical,
        AuditLevel = JobAuditLevelCode.Failures,
        AlertProfile = AlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.Hourly)]
    public async Task Handle(JobContext ctx, CancellationToken ct)
    {
        var result = await store.PurgeExpiredDataAsync(
            new PurgeExpiredDataCommand(
                ctx.NamespaceId,
                _eventsRetentionDays,
                _alertRetentionDays,
                _workerRetentionSeconds,
                BatchSize,
                MaxIterations
            ),
            ct
        );

        if (result.UndeliveredAlertsPurged > 0)
        {
            LogUndeliveredPurge(ctx, result.UndeliveredAlertsPurged);
        }
    }

    // One line per pass, never one per row: a namespace whose channel has been down for a quarter would
    // otherwise log a page of them. Warning, because an alert deleted before it ever reached a channel
    // is a signal an operator never got - and never an Acta alert of its own, which would recurse.
    private void LogUndeliveredPurge(JobContext ctx, int count) =>
        _log.LogWarning(
            "ACTA sys.retention: purged {Count} alerts in namespace ({Namespace}) that aged out before delivery settled; reason ({Reason}).",
            count,
            ctx.JobNamespace,
            "alert-retention-cap"
        );
}
