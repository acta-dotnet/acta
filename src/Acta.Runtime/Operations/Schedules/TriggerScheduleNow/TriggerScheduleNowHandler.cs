namespace Acta.Runtime;

/// <summary>
/// <see cref="ISchedules.TriggerNowAsync"/>: one routine call pulls the slot cursor to now. The paused and
/// in-flight guards live in the routine alone, so transition legality has one home.
/// </summary>
internal sealed class TriggerScheduleNowHandler(
    ScheduleTargetResolution targets,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    private static readonly TriggerScheduleNowResponse NotFound = new(ControlAction.NotFound, null, null, null, null);

    public async ValueTask<TriggerScheduleNowResponse> HandleAsync(TriggerScheduleNowCommand command, CancellationToken ct)
    {
        if (await targets.ResolveAsync(command.Schedule, ct) is not { } target)
        {
            return NotFound;
        }

        var name = target.Schedule.Name;
        var row = await session.ExecuteOneAsync(
            new StoreCommand("TriggerScheduleNow"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, target.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.Name, name));
                cmd.AddOperatorAudit(
                    dialect,
                    command.ActorKey,
                    command.ReasonMessage is null ? name : $"{name}: {ScheduleControl.RowReason(command.ReasonMessage)}"
                );
            },
            DbProjectionResolver.Resolve<ScheduleControlRow>(),
            ct
        );
        if (row.Action == ControlAction.Applied)
        {
            // A trigger always makes the slot due now, so unlike the other verbs this wake does not wait
            // on an upcoming slot cursor.
            await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return new TriggerScheduleNowResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextRunAtUtc, row.Version);
    }
}
