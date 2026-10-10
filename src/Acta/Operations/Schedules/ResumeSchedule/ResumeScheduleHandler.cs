namespace Acta;

/// <summary>
/// <see cref="ISchedules.ResumeAsync"/>: reconciles the schedule's stored cursor by its misfire policy
/// (Skip jumps forward, CatchUpOnce fires once) and recomputes the slot cursor over the now-active set,
/// then one routine call sets both and records <c>schedule.resumed</c> against the slot job.
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

        var t = target.Schedule;
        var reconciled = NextOccurrenceCalculator.Reconcile(
            t.Expression,
            t.TimeZoneId,
            t.ExpressionKind,
            t.MisfireStrategy,
            t.NextRunAtUtc,
            target.NowUtc
        );
        var jobNextRun = target.SlotNextRunWith(s =>
            s with
            {
                Status = ScheduleStatusCode.Active,
                PausedUntilUtc = null,
                NextRunAtUtc = reconciled,
            }
        );

        var row = await session.ExecuteOneAsync(
            new StoreCommand("ResumeSchedule"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.JobId, target.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.Name, t.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.NextRunAtUtc, reconciled));
                ScheduleControl.BindTail(cmd, dialect, jobNextRun, command.ActorKey, command.ReasonMessage);
            },
            DbProjectionResolver.Resolve<ResumeScheduleRow>(),
            ct
        );
        await ScheduleControl.WakeIfDueAsync(wakeup, jobNextRun, row.Action, ct);
        return new ResumeScheduleResponse(row.Action, row.Status, row.PausedUntilUtc, row.NextRunAtUtc, row.Version);
    }
}
