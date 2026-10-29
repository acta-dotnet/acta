namespace Acta;

/// <summary>
/// Finds the live schedule a path names: the definition's permanent slot, and among the slot's live (non-orphaned)
/// schedules the one named, with the database clock a verb decides against. Null when the definition, its slot, or
/// the schedule is absent; each surfaces as NotFound.
/// </summary>
internal sealed class ResolveScheduleTargetHandler(
    ResolveSlotIdHandler resolveSlotId,
    IActaClock clock,
    GetLiveSchedulesHandler liveSchedules
)
{
    public async ValueTask<ResolveScheduleTargetResponse?> HandleAsync(ResolveScheduleTargetQuery query, CancellationToken ct)
    {
        var schedule = query.Schedule;
        ArgumentNullException.ThrowIfNull(schedule);
        var jobNamespace = NameSyntax.Canonicalize(schedule.JobNamespace, nameof(schedule.JobNamespace));
        var jobName = NameSyntax.Canonicalize(schedule.JobName, nameof(schedule.JobName));
        var scheduleName = NameSyntax.Canonicalize(
            schedule.ScheduleName,
            nameof(schedule.ScheduleName),
            maxLength: NameSyntax.ExtendedMaxLength
        );

        if (await resolveSlotId.HandleAsync(new ResolveSlotIdQuery(jobNamespace, jobName), ct) is not { } jobId)
        {
            return null;
        }

        var nowUtc = await clock.GetUtcNowAsync(ct);
        var live = (await liveSchedules.HandleAsync(new GetLiveSchedulesQuery(jobId), ct)).Schedules;
        var target = live.FirstOrDefault(s => s.ScheduleName == scheduleName);
        return target is null ? null : new ResolveScheduleTargetResponse(jobId, nowUtc, live, target);
    }
}
