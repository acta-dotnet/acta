using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// <c>sys.outbox</c>, the external-outbox relay: a recurring, competitively claimed <c>[Job]</c> added to a namespace only by
/// <c>AddOutboxRelay</c>. Each tick applies parked operator commands (<see cref="OutboxSignalApplier"/>) first, so rows a
/// requeue frees are claimable in the same pass, drains a bounded slice of due source rows through <see cref="OutboxRelay"/>,
/// and writes its accounting to the slot's <see cref="OutboxTickSummary.VariableName"/> variable for operator surfaces on any
/// peer. <c>EventRecording = Failures</c> keeps idle and successful ticks out of <c>events</c>, while quarantine and
/// infrastructure failures fail the tick so <c>SysCritical</c> alerts; staging sends no wakeup, so the cadence is the pickup floor.
/// </summary>
internal sealed class OutboxJob(OutboxRelayRegistry registry, OutboxOperations outbox, IOptions<JobsOptions> options)
{
    [Job(
        "sys.outbox",
        Priority = JobPriorityCode.Realtime,
        EventRecording = EventRecordingCode.Failures,
        AlertProfile = AlertProfileCode.SysCritical
    )]
    [Schedule("default", Cron.Every5Seconds)]
    public async Task Handle(JobContext ctx, CancellationToken ct)
    {
        // The relay is the executing namespace's own: resolve THIS namespace's registration and a source
        // store + service bound to it, so a multi-Run host drains each namespace's source independently.
        var registration = registry.Registration(ctx.JobNamespace);
        var service = registry.Service(ctx.JobNamespace);

        var (requeued, discarded) = await new OutboxSignalApplier(service.Source, outbox).ApplyAsync(ctx.JobId, ct);

        var summary = await service.RunTickAsync(
            new OutboxRelayTickOptions(
                registration.SourceName,
                registration.QuarantineThreshold,
                options.Value.LeaseTtlSeconds,
                options.Value.MaxInlinePayloadBytes
            ),
            ct
        );

        // A failed tick throws above and leaves the previous status standing. The summary is serialized with Acta's
        // own context rather than the app's payload serializer, so every peer reads it back the same way.
        var status = JsonSerializer.SerializeToUtf8Bytes(
            summary with
            {
                Requeued = requeued,
                Discarded = discarded,
            },
            OutboxTickSummaryJsonContext.Default.OutboxTickSummary
        );
        await ctx.SetVariableAsync(OutboxTickSummary.VariableName, JobPayload.Trusted(JobPayloadFormat.Json, status), ct);
    }
}

/// <summary>
/// Applies a <c>sys.outbox</c> slot's parked operator commands to its source, requeue then discard. Per command: store
/// the Quarantined rows it targets as its plan in its own row, apply it to those rows at the source, then consume it
/// with its evidence event in one ledger statement. A pass that finds the command planned replays the plan, which skips
/// rows already moved (both verbs match Quarantined rows only), and its event names the planned rows. A pass whose read
/// no longer matches the stored command stops before the source when it would plan, and records nothing when it would
/// consume.
/// </summary>
internal sealed class OutboxSignalApplier(OutboxOperations.Source source, OutboxOperations outbox)
{
    public async Task<(int Requeued, int Discarded)> ApplyAsync(long slotJobId, CancellationToken ct)
    {
        var requeued = await ApplyOneAsync(slotJobId, OutboxSignalNames.Requeue, ct);
        var discarded = await ApplyOneAsync(slotJobId, OutboxSignalNames.Discard, ct);
        return (requeued, discarded);
    }

    private async Task<int> ApplyOneAsync(long slotJobId, string name, CancellationToken ct)
    {
        var row = (await outbox.GetOutboxSignalAsync(slotJobId, name, ct))?.Signal;
        if (row is null)
        {
            return 0;
        }

        OutboxSignalPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(row.Value, OutboxSignalJsonContext.Default.OutboxSignalPayload);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null)
        {
            // A malformed command must not wedge the two-name inbox: consume it, leave the trail.
            await outbox.ConsumeOutboxSignalAsync(
                new ConsumeOutboxSignalCommand(
                    slotJobId,
                    name,
                    row.Value,
                    EventCodeFor(name),
                    null,
                    "Malformed operator command discarded unapplied."
                ),
                ct
            );
            return 0;
        }

        var value = row.Value;
        var planned = payload.PlannedIds;
        if (planned is null)
        {
            planned = (await source.ListQuarantinedIdsAsync(new ListQuarantinedOutboxIdsQuery(payload.OutboxIds), ct)).OutboxIds;
            var plannedValue = JsonSerializer.SerializeToUtf8Bytes(
                payload with
                {
                    PlannedIds = planned,
                },
                OutboxSignalJsonContext.Default.OutboxSignalPayload
            );
            if (!(await outbox.PlanOutboxSignalAsync(slotJobId, name, value, plannedValue, ct)).Planned)
            {
                return 0;
            }

            value = plannedValue;
        }

        var affected =
            name == OutboxSignalNames.Requeue
                ? (await source.RequeueQuarantinedAsync(new RequeueQuarantinedOutboxCommand(planned), ct)).OutboxIds
                : (await source.DiscardQuarantinedAsync(new DiscardQuarantinedOutboxCommand(planned), ct)).OutboxIds;

        // A pass that found the command planned changes only what an earlier pass left undone, so its evidence is
        // the plan.
        var evidence = payload.PlannedIds is null ? affected : planned;
        var consumed = await outbox.ConsumeOutboxSignalAsync(
            new ConsumeOutboxSignalCommand(
                slotJobId,
                name,
                value,
                EventCodeFor(name),
                payload.Actor,
                Evidence(payload.ReasonMessage, evidence)
            ),
            ct
        );
        return consumed.Consumed ? evidence.Count : 0;
    }

    private static EventCode EventCodeFor(string name) =>
        name == OutboxSignalNames.Requeue ? EventCode.OutboxRequeued : EventCode.OutboxDiscarded;

    /// <summary>
    /// The evidence line: operator justification first, then the applied count and the bounded id
    /// sample (the same 10-id bound the quarantine alert uses), truncated to the events column width.
    /// </summary>
    private static string Evidence(string? reasonMessage, IReadOnlyList<Guid> affected)
    {
        var applied = $"{affected.Count} row(s): [{OutboxQuarantineTickException.FormatSample(affected)}]";
        var text = string.IsNullOrEmpty(reasonMessage) ? applied : $"{reasonMessage} - {applied}";
        return text.Truncate(ActaTextLimits.EventMessage)!;
    }
}
