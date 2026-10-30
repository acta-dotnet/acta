using System.Globalization;

namespace Acta;

/// <summary>
/// <see cref="ISchedules.TriggerNowAsync"/>: a manual firing, an envelope with no contributors. On a Permanent
/// definition it fires the slot when the slot is Idle; a busy, held, cancelled, or finished slot is Rejected. On a
/// PerOccurrence definition it admits a manual root through the admission routine, charged to the schedule; a full
/// schedule is Rejected. A paused schedule is Rejected either way, and no schedule cursor moves.
/// </summary>
internal sealed class TriggerScheduleNowHandler(
    ResolveScheduleTargetHandler resolveScheduleTarget,
    AdmitFiringsHandler admitFirings,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    private static readonly TriggerScheduleNowResponse NotFound = new(ControlAction.NotFound, null, null, null, null);

    /// <summary>Reads of a per-occurrence schedule whose state another writer moved between the read and the apply.</summary>
    private const int MaxReplans = 2;

    public async ValueTask<TriggerScheduleNowResponse> HandleAsync(TriggerScheduleNowCommand command, CancellationToken ct)
    {
        for (var replan = 0; ; replan++)
        {
            if (await resolveScheduleTarget.HandleAsync(new ResolveScheduleTargetQuery(command.Schedule), ct) is not { } target)
            {
                return NotFound;
            }

            if (target.JobId is { } slotId)
            {
                return await FireSlotAsync(command, target, slotId, ct);
            }

            if (target.Status == ScheduleStatusCode.Paused || target.OutstandingCount >= target.MaxOutstanding)
            {
                return Answer(ControlAction.Rejected, target);
            }

            if (await AdmitRootAsync(command, target, ct))
            {
                await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
                return Answer(ControlAction.Applied, target);
            }

            if (replan == MaxReplans)
            {
                return Answer(ControlAction.Rejected, target);
            }
        }
    }

    /// <summary>A manual root: one admitting group with no member rows, keyed by its firing so it is never deduplicated.</summary>
    private async Task<bool> AdmitRootAsync(TriggerScheduleNowCommand command, ResolveScheduleTargetResponse target, CancellationToken ct)
    {
        var firingId = Guid.CreateVersion7();
        var group = new AdmitFiringsGroup(
            Ordinal: 0,
            JobId: null,
            ExpectedJobVersion: null,
            ScheduleId: target.ScheduleId,
            ExpectedStateVersion: target.StateVersion,
            Admit: true,
            Envelope: new FiringEnvelope(firingId, FiringEnvelope.ManualKind, target.NowUtc, []).ToJson(),
            JobRef: Guid.CreateVersion7(),
            DeduplicationKey: string.Create(CultureInfo.InvariantCulture, $"sys.manual.{target.ScheduleId}.{firingId:N}"),
            TriggerMessage: command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
            TriggerDetails: ScheduleControl.DetailsText(target.ScheduleName, command.Actor)
        );
        var outcome = await admitFirings.HandleAsync(new AdmitFiringsCommand(target.NamespaceId, [group], []), ct);
        return outcome.Groups[0].Applied;
    }

    private async Task<TriggerScheduleNowResponse> FireSlotAsync(
        TriggerScheduleNowCommand command,
        ResolveScheduleTargetResponse target,
        long slotId,
        CancellationToken ct
    )
    {
        var name = target.ScheduleName;
        var envelope = new FiringEnvelope(Guid.CreateVersion7(), FiringEnvelope.ManualKind, target.NowUtc, []).ToJson();
        var row = await session.ExecuteOneAsync(
            new StoreCommand("TriggerScheduleNow"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, slotId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleId, target.ScheduleId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.FiringEnvelope, envelope));
                cmd.AddEventFields(
                    dialect,
                    command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    ScheduleControl.Details(name, command.Actor)
                );
            },
            DbProjectionResolver.Resolve<TriggerScheduleNowRow>(),
            ct
        );
        await JobControl.WakeWorkersIfAsync(wakeup, row.Action == ControlAction.Applied, ct);
        return new TriggerScheduleNowResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextOccurrenceAtUtc, row.Version);
    }

    private static TriggerScheduleNowResponse Answer(ControlAction action, ResolveScheduleTargetResponse target) =>
        new(action, target.Status, target.PausedUntilUtc, target.NextOccurrenceAtUtc, target.Version);
}
