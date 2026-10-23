namespace Acta;

/// <summary>
/// <see cref="ISchedules.TriggerNowAsync"/>: one routine call pulls the slot cursor to now. The paused and
/// in-flight guards live in the routine alone, so transition legality has one home.
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
        var row = await session.ExecuteOneAsync(
            new StoreCommand("TriggerScheduleNow"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, target.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleName, name));
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
            // A trigger always makes the slot due now, so unlike the other verbs this wake does not wait
            // on an upcoming slot cursor.
            await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return new TriggerScheduleNowResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextOccurrenceAtUtc, row.Version);
    }
}
