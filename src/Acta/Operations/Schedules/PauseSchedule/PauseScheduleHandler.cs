namespace Acta;

/// <summary>
/// <see cref="ISchedules.PauseAsync"/>: pauses one schedule, indefinitely or until an instant, and parks its next
/// evaluation at that instant. The slot is not touched: a firing the schedule already admitted runs on, and the other
/// schedules of the slot keep their own cursors. One routine call writes the schedule and its state and records
/// <c>schedule.paused</c> against the slot job, or against the definition when the schedule has no slot.
/// </summary>
internal sealed class PauseScheduleHandler(ResolveScheduleTargetHandler resolveScheduleTarget, IDbSession session, ISqlDialect dialect)
{
    private static readonly PauseScheduleResponse NotFound = new(ControlAction.NotFound, null, null, null, null);
    private static readonly PauseScheduleResponse Rejected = new(ControlAction.Rejected, null, null, null, null);

    public async ValueTask<PauseScheduleResponse> HandleAsync(PauseScheduleCommand command, CancellationToken ct)
    {
        if (await resolveScheduleTarget.HandleAsync(new ResolveScheduleTargetQuery(command.Schedule), ct) is not { } target)
        {
            return NotFound;
        }

        if (command.UntilUtc is { } until && until <= target.NowUtc)
        {
            return Rejected;
        }

        var name = target.ScheduleName;
        var row = await session.ExecuteOneAsync(
            new StoreCommand("PauseSchedule"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleId, target.ScheduleId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.PausedUntilUtc, command.UntilUtc));
                cmd.Parameters.Add(
                    dialect.CreateParameter(ActaSchema.Schedule.PauseMessage, ScheduleControl.Reason(command.ReasonMessage))
                );
                ScheduleControl.BindTail(cmd, dialect, name, command.Actor, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<PauseScheduleRow>(),
            ct
        );
        return new PauseScheduleResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextOccurrenceAtUtc, row.Version);
    }
}
