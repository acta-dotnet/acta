using Microsoft.Extensions.Options;

namespace Acta.Features.Outbox;

/// <summary>
/// <c>sys.outbox</c>, the external-outbox relay. A recurring, competitively-claimed <c>[Job]</c> added
/// to a namespace only when a worker calls <c>AddOutboxRelay</c>. Each tick drains a bounded slice of
/// due source rows through <see cref="OutboxRelayService"/> and returns the tick's accounting line
/// (<c>claimed/relayed/dedup/quarantined/backlog</c>) as its result, which the recurring slot retains,
/// so the dashboard job detail shows the last successful tick. <c>AuditLevel.Failures</c> keeps idle and
/// successful ticks out of <c>events</c>, while quarantine and infrastructure failures fail the tick so
/// the <c>SysCritical</c> alert path fires. The five-second cadence plus normal worker discovery is the
/// expected pickup latency floor, since cross-database producer staging sends no wakeup.
/// </summary>
internal sealed class OutboxRelayJob(OutboxRelayRegistry registry, IOptions<JobsOptions> options)
{
    [Job(
        "sys.outbox",
        Priority = JobPriorityCode.Critical,
        AuditLevel = JobAuditLevelCode.Failures,
        AlertProfile = JobAlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.Every5Seconds)]
    public async Task<string> Handle(JobContext ctx, CancellationToken ct)
    {
        // The relay is the executing namespace's own: resolve THIS namespace's registration and a source
        // store + service bound to it, so a multi-Run host drains each namespace's source independently.
        var registration = registry.Registration(ctx.JobNamespace);
        var summary = await registry
            .Service(ctx.JobNamespace)
            .RunTickAsync(
                new OutboxRelayTickOptions(
                    registration.SourceName,
                    registration.QuarantineThreshold,
                    options.Value.LeaseTtlSeconds,
                    options.Value.MaxInlinePayloadBytes
                ),
                ct
            );
        return summary.ToString();
    }
}
