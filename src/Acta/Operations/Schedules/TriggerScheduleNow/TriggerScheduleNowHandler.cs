namespace Acta;

/// <summary>
/// <see cref="ISchedules.TriggerNowAsync"/>: admits a manual firing of the schedule's slot, an envelope with no
/// contributors, when the slot is Idle; a busy, held, cancelled, or finished slot and a paused schedule are Rejected.
/// No schedule cursor moves: the next scheduled occurrence stays where the engine left it.
/// </summary>
internal sealed class TriggerScheduleNowHandler(
    ResolveScheduleTargetHandler resolveScheduleTarget,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    private static readonly TriggerScheduleNowResponse NotFound = new(ControlAction.NotFound, null, null, null, null);

    public async ValueTask<TriggerScheduleNowResponse> HandleAsync(TriggerScheduleNowCommand command, CancellationToken ct)
    {
        if (await resolveScheduleTarget.HandleAsync(new ResolveScheduleTargetQuery(command.Schedule), ct) is not { } target)
        {
            return NotFound;
        }

        var name = target.Schedule.ScheduleName;
        var envelope = new FiringEnvelope(Guid.CreateVersion7(), FiringEnvelope.ManualKind, target.NowUtc, []).ToJson();
        var row = await session.ExecuteOneAsync(
            new StoreCommand("TriggerScheduleNow"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, target.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleName, name));
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
        if (row.Action == ControlAction.Applied)
        {
            await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return new TriggerScheduleNowResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextOccurrenceAtUtc, row.Version);
    }
}
