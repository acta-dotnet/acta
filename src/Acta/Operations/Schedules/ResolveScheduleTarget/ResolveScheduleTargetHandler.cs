namespace Acta;

/// <summary>
/// Finds the live schedule a lookup names, with what a control verb needs to recompute the slot cursor in C#, cron
/// math the database cannot do: the owning recurring slot job, the database clock, and the slot's live (non-orphaned)
/// schedules; no SQL of its own. Null when the job or the schedule is absent; both surface as NotFound.
/// </summary>
internal sealed class ResolveScheduleTargetHandler(GetJobIdHandler getJobId, IActaClock clock, GetLiveSchedulesHandler liveSchedules)
{
    public async ValueTask<ResolveScheduleTargetResponse?> HandleAsync(ResolveScheduleTargetQuery query, CancellationToken ct)
    {
        var schedule = query.Schedule;
        ArgumentNullException.ThrowIfNull(schedule);
        var scheduleName = NameSyntax.Canonicalize(
            schedule.ScheduleName,
            nameof(schedule.ScheduleName),
            maxLength: NameSyntax.ExtendedMaxLength
        );

        if (await getJobId.HandleAsync(new GetJobIdQuery(schedule.Job), ct) is not { JobId: var jobId })
        {
            return null;
        }

        var nowUtc = await clock.GetUtcNowAsync(ct);
        var live = (await liveSchedules.HandleAsync(new GetLiveSchedulesQuery(jobId), ct)).Schedules;
        var target = live.FirstOrDefault(s => s.Name == scheduleName);
        return target is null ? null : new ResolveScheduleTargetResponse(jobId, nowUtc, live, target);
    }
}
