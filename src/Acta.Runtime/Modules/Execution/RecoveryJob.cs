namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// <c>sys.recovery</c>, the system recovery job. A recurring, competitively-claimed <c>[Job]</c>
/// auto-registered into every worker namespace; whichever live worker claims a tick runs one
/// <see cref="RecoveryPass"/>. Leaderless: the recurring slot's single claim per tick is the only
/// coordination, so exactly one pass runs per namespace per tick regardless of how many workers are live.
/// </summary>
internal sealed class RecoveryJob(RecoveryPass pass)
{
    /// <summary>
    /// Runs one recovery pass for the firing namespace. <c>AuditLevel.Failures</c> keeps idle ticks out
    /// of <c>events</c>: reclaim emits per-job orphaned <c>job.execution-finished</c> events on affected
    /// jobs, so a quiet pass writes nothing.
    /// </summary>
    [Job(
        "sys.recovery",
        Priority = JobPriorityCode.Critical,
        AuditLevel = JobAuditLevelCode.Failures,
        AlertProfile = AlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.EveryMinute)]
    public Task Handle(JobContext ctx, CancellationToken ct) => pass.RunAsync(ctx.NamespaceId, ctx.JobNamespace, ct);
}
