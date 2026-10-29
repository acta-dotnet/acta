namespace Acta;

/// <summary>
/// <see cref="ISchedules.ResumeAsync"/>: lifts the pause and hands the schedule back to the admission engine, which
/// applies its misfire policy at the next pass; the remembered cursor is not moved here. One routine call writes the
/// schedule and its state and records <c>schedule.resumed</c> against the slot job, or against the definition when the
/// schedule has no slot.
/// </summary>
internal sealed class ResumeScheduleHandler(
    ResolveScheduleTargetHandler resolveScheduleTarget,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    private static readonly ResumeScheduleResponse NotFound = new(ControlAction.NotFound, null, null, null, null);

    public async ValueTask<ResumeScheduleResponse> HandleAsync(ResumeScheduleCommand command, CancellationToken ct)
    {
        if (await resolveScheduleTarget.HandleAsync(new ResolveScheduleTargetQuery(command.Schedule), ct) is not { } target)
        {
            return NotFound;
        }

        var name = target.Schedule.ScheduleName;
        var row = await session.ExecuteOneAsync(
            new StoreCommand("ResumeSchedule"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleId, target.ScheduleId));
                ScheduleControl.BindTail(cmd, dialect, name, command.Actor, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<ResumeScheduleRow>(),
            ct
        );
        if (row.Action == ControlAction.Applied)
        {
            // The admitter listens on the same channel as the claim loop; it evaluates the schedule at once.
            await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
        }

        return new ResumeScheduleResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextOccurrenceAtUtc, row.Version);
    }
}
