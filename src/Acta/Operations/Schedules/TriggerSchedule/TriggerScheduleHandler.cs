using System.Globalization;

namespace Acta;

/// <summary>
/// <see cref="ISchedules.TriggerAsync"/>: a manual firing, an envelope with no contributors. On a Permanent
/// definition it fires the slot when the slot is Idle; a busy, held, cancelled, or finished slot is Rejected. On a
/// PerOccurrence definition it admits a manual root through the admission routine, charged to the schedule; a full
/// schedule is Rejected. A paused schedule, or one in a suspended namespace, is Rejected either way, and no schedule
/// cursor moves.
/// </summary>
internal sealed class TriggerScheduleHandler(
    GetScheduleTargetHandler getScheduleTarget,
    AdmitScheduleFiringsHandler admitScheduleFirings,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    private static readonly TriggerScheduleResponse NotFound = new(ControlAction.NotFound, null, null, null, null, null);

    /// <summary>Reads of a per-occurrence schedule whose state another writer moved between the read and the apply.</summary>
    private const int MaxReplans = 2;

    public async ValueTask<TriggerScheduleResponse> HandleAsync(TriggerScheduleCommand command, CancellationToken ct)
    {
        for (var replan = 0; ; replan++)
        {
            if (await getScheduleTarget.HandleAsync(new GetScheduleTargetQuery(command.Schedule), ct) is not { } target)
            {
                return NotFound;
            }

            if (target.NamespaceStatus != NamespaceStatusCode.Active)
            {
                return Answer(ControlAction.Rejected, target);
            }

            if (target.JobId is { } slotId)
            {
                return await FireSlotAsync(command, target, slotId, ct);
            }

            if (target.Status == ScheduleStatusCode.Paused || target.OutstandingCount >= target.MaxOutstanding)
            {
                return Answer(ControlAction.Rejected, target);
            }

            if (await AdmitRootAsync(command, target, ct) is { } root)
            {
                await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
                return Answer(ControlAction.Applied, target, root);
            }

            if (replan == MaxReplans)
            {
                return Answer(ControlAction.Rejected, target);
            }
        }
    }

    /// <summary>
    /// A manual root: one admitting group, keyed by its firing so it is never deduplicated, and its schedule as a
    /// check-only row, so a pause or a lowered capacity committed after the read rejects it. Answers the root's ref when
    /// the group applied.
    /// </summary>
    private async Task<JobRef?> AdmitRootAsync(TriggerScheduleCommand command, GetScheduleTargetResponse target, CancellationToken ct)
    {
        var firingId = Guid.CreateVersion7();
        var root = JobRef.New();
        var group = new AdmitScheduleFiringsGroup(
            Ordinal: 0,
            JobId: null,
            ExpectedJobVersion: null,
            ScheduleId: target.ScheduleId,
            ExpectedStateVersion: target.StateVersion,
            Admit: true,
            Envelope: new FiringEnvelope(firingId, FiringEnvelope.ManualKind, target.NowUtc, []).ToJson(),
            JobRef: root.Value,
            DeduplicationKey: string.Create(CultureInfo.InvariantCulture, $"sys.manual.{target.ScheduleId}.{firingId:N}"),
            TriggerMessage: command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
            TriggerActor: JobControlActor.Operator(command.Actor),
            TriggerDetails: ScheduleControl.DetailsText(target.ScheduleName)
        );
        var read = AdmitScheduleFiringsValues.Check(0, target.ScheduleId, target.ScheduleName, target.StateVersion, target.Version);
        var outcome = await admitScheduleFirings.HandleAsync(new AdmitScheduleFiringsCommand(target.NamespaceId, [group], [read]), ct);
        return outcome.Groups[0].Applied ? root : null;
    }

    private async Task<TriggerScheduleResponse> FireSlotAsync(
        TriggerScheduleCommand command,
        GetScheduleTargetResponse target,
        long slotId,
        CancellationToken ct
    )
    {
        var name = target.ScheduleName;
        var envelope = new FiringEnvelope(Guid.CreateVersion7(), FiringEnvelope.ManualKind, target.NowUtc, []).ToJson();
        var row = await session.ExecuteOneAsync(
            new StoreCommand("TriggerSchedule"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, slotId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleId, target.ScheduleId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.FiringEnvelope, envelope));
                cmd.AddEventFields(
                    dialect,
                    command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    JobControlActor.Operator(command.Actor),
                    ScheduleControl.Details(name)
                );
            },
            DbProjectionResolver.Resolve<TriggerScheduleRow>(),
            ct
        );
        await JobControl.WakeWorkersIfAsync(wakeup, row.Action == ControlAction.Applied, ct);
        return new TriggerScheduleResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextOccurrenceAtUtc, row.Version, row.JobRef);
    }

    private static TriggerScheduleResponse Answer(ControlAction action, GetScheduleTargetResponse target, JobRef? jobRef = null) =>
        new(action, target.Status, target.PausedUntilUtc, target.NextOccurrenceAtUtc, target.Version, jobRef);
}
