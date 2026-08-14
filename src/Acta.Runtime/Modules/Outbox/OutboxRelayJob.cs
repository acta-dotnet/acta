using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Outbox;

/// <summary>
/// <c>sys.outbox</c>, the external-outbox relay. A recurring, competitively-claimed <c>[Job]</c> added
/// to a namespace only when a worker calls <c>AddOutboxRelay</c>. Each tick first applies any parked
/// operator commands (the two-name signal inbox: requeue before the drain so freed rows are claimable
/// in the same pass, then discard), then drains a bounded slice of due source rows through
/// <see cref="OutboxRelayService"/> and returns the tick's accounting line as its result, which the
/// recurring slot retains, so operator surfaces on any peer read backlog and quarantine from it.
/// <c>AuditLevel.Failures</c> keeps idle and successful ticks out of <c>events</c> (applied commands
/// write their own always-emitted evidence events), while quarantine and infrastructure failures fail
/// the tick so the <c>SysCritical</c> alert path fires. The five-second cadence plus normal worker
/// discovery is the expected pickup latency floor, since cross-database producer staging sends no wakeup.
/// </summary>
internal sealed class OutboxRelayJob(OutboxRelayRegistry registry, IOutboxSignalStore signals, IOptions<JobsOptions> options)
{
    [Job(
        "sys.outbox",
        Priority = JobPriorityCode.Critical,
        AuditLevel = JobAuditLevelCode.Failures,
        AlertProfile = AlertProfileCode.SysCritical
    )]
    [JobSchedule("default", Cron.Every5Seconds)]
    public async Task<string> Handle(JobContext ctx, CancellationToken ct)
    {
        // The relay is the executing namespace's own: resolve THIS namespace's registration and a source
        // store + service bound to it, so a multi-Run host drains each namespace's source independently.
        var registration = registry.Registration(ctx.JobNamespace);
        var service = registry.Service(ctx.JobNamespace);

        var (requeued, discarded) = await service.ApplyOperatorSignalsAsync(ctx.JobId, signals, ct);

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
