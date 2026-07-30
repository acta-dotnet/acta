using Acta.Configuration;
using Microsoft.Extensions.Options;

namespace Acta.Maintenance;

/// <summary>
/// <c>sys.retention</c>, the system retention sweep. A recurring, competitively-claimed
/// <c>[Job]</c> auto-registered into every worker namespace; whichever live worker claims the hourly
/// tick runs one namespace-scoped <c>purge_expired_data</c> pass. Leaderless: the recurring slot's
/// single claim per tick is the only coordination, so exactly one pass runs per namespace per tick.
/// </summary>
/// <remarks>
/// One pass deletes, in bounded batches: terminal <c>job</c> rows past their <c>retention_until_utc</c>
/// (cascading the per-job child tables), <c>events</c> rows older than
/// <c>JobsOptions.JobEventsRetentionDays</c>, settled <c>alerts</c> rows older than
/// <c>JobsOptions.AlertRetentionDays</c>, and Dead <c>workers</c> rows older than
/// <c>JobsOptions.WorkerRetention</c>, then reaps expired <c>leases</c> lock rows globally. The batch
/// and iteration caps bound each tick; the next hourly fire continues any backlog. <c>AuditLevel.Failures</c>
/// keeps idle ticks out of <c>events</c>: a purge pass emits no per-row events.
/// </remarks>
internal sealed class RetentionJob(IRetentionStore store, IOptions<JobsOptions> options)
{
    // Per-tick bound: at most BatchSize * MaxIterations rows deleted per section; the next hourly fire
    // continues any backlog rather than letting one tick run unbounded.
    private const int BatchSize = 1000;
    private const int MaxIterations = 50;

    private readonly int _eventsRetentionDays = options.Value.JobEventsRetentionDays;
    private readonly int _alertRetentionDays = options.Value.AlertRetentionDays;
    private readonly int _workerRetentionSeconds = (int)options.Value.WorkerRetention.TotalSeconds;

    /// <summary>
    /// Runs one retention sweep for the firing namespace: terminal jobs, expired events, settled alerts,
    /// dead workers.
    /// </summary>
    [Job(
        "sys.retention",
        Priority = JobPriorityCode.Critical,
        AuditLevel = JobAuditLevelCode.Failures,
        AlertProfile = JobAlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.Hourly)]
    public Task Handle(JobContext ctx, CancellationToken ct)
    {
        return store.PurgeExpiredDataAsync(
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
    }
}
