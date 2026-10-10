using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// <c>sys.outbox</c>, the external-outbox relay: a recurring, competitively claimed <c>[Job]</c> added to a namespace
/// only by <c>AddOutboxRelay</c>. Each tick applies parked operator commands (requeue first, so freed rows are claimable
/// in the same pass, then discard), drains a bounded slice of due source rows through <see cref="OutboxRelay"/>, and
/// returns the tick's accounting line as its result, from which operator surfaces on any peer read backlog and
/// quarantine. <c>AuditLevel.Failures</c> keeps idle and successful ticks out of <c>events</c>, while quarantine and
/// infrastructure failures fail the tick so <c>SysCritical</c> alerts; staging sends no wakeup, so the cadence is the pickup floor.
/// </summary>
internal sealed class OutboxJob(OutboxRelayRegistry registry, OutboxOperations outbox, IOptions<JobsOptions> options)
{
    [Job(
        "sys.outbox",
        Priority = JobPriorityCode.Critical,
        AuditLevel = DefinitionAuditLevelCode.Failures,
        AlertProfile = AlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.Every5Seconds)]
    public async Task<string> Handle(JobContext ctx, CancellationToken ct)
    {
        // The relay is the executing namespace's own: resolve THIS namespace's registration and a source
        // store + service bound to it, so a multi-Run host drains each namespace's source independently.
        var registration = registry.Registration(ctx.JobNamespace);
        var service = registry.Service(ctx.JobNamespace);

        var (requeued, discarded) = await service.ApplyOperatorSignalsAsync(ctx.JobId, outbox, ct);

        var summary = await service.RunTickAsync(
            new OutboxRelayTickOptions(
                registration.SourceName,
                registration.QuarantineThreshold,
                options.Value.LeaseTtlSeconds,
                options.Value.MaxInlinePayloadBytes
            ),
            ct
        );
        return (summary with { Requeued = requeued, Discarded = discarded }).ToString();
    }
}
