namespace Acta.Runtime;

/// <summary>
/// <see cref="ISchedules.PauseAsync"/>: recomputes the slot cursor with the schedule paused, then one
/// routine call sets the schedule Paused and the slot cursor, and records <c>schedule.paused</c> against
/// the slot job. The schedule's own cursor is left alone.
/// </summary>
internal sealed class PauseScheduleHandler(
    ScheduleTargetResolution targets,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    private static readonly PauseScheduleResponse NotFound = new(ControlAction.NotFound, null, null, null, null);
    private static readonly PauseScheduleResponse Rejected = new(ControlAction.Rejected, null, null, null, null);

    public async ValueTask<PauseScheduleResponse> HandleAsync(PauseScheduleCommand command, CancellationToken ct)
    {
        if (await targets.ResolveAsync(command.Schedule, ct) is not { } target)
        {
            return NotFound;
        }

        if (command.UntilUtc is { } until && until <= target.NowUtc)
        {
            return Rejected;
        }

        // An indefinite pause drops out of the slot MIN; a timed pause contributes its wake instant.
        var jobNextRun = target.SlotNextRunWith(s => s with { Status = ScheduleStatusCode.Paused, PausedUntilUtc = command.UntilUtc });

        var row = await session.ExecuteOneAsync(
            new StoreCommand("PauseSchedule"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, target.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.Name, target.Schedule.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.PausedUntilUtc, command.UntilUtc));
                ScheduleControl.BindTail(cmd, dialect, jobNextRun, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<PauseScheduleRow>(),
            ct
        );
        await ScheduleControl.WakeIfDueAsync(wakeup, jobNextRun, row.Action, ct);
        return new PauseScheduleResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextRunAtUtc, row.Version);
    }
}
