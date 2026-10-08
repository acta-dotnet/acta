namespace Acta;

/// <summary>
/// Finds the live schedule a lookup names, with what a control verb needs to recompute the slot cursor in
/// C#, cron math the database cannot do: the owning recurring slot job, the database clock, and the slot's
/// live (non-orphaned) schedules. Null when the job or the schedule is absent; both surface as NotFound.
/// </summary>
internal sealed class ScheduleTargetResolution(JobLookupResolution jobs, IActaClock clock, GetLiveSchedulesHandler liveSchedules)
{
    public async ValueTask<ScheduleTarget?> ResolveAsync(ScheduleLookup schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var scheduleName = IdentifierSyntax.CanonicalizeKebab(
            schedule.ScheduleName,
            nameof(schedule.ScheduleName),
            IdentifierSyntax.ExtendedMaxLength
        );

        var jobId = await jobs.ResolveAsync(schedule.Job, ct);
        if (jobId is null)
        {
            return null;
        }

        var nowUtc = await clock.GetUtcNowAsync(ct);
        var live = (await liveSchedules.HandleAsync(new GetLiveSchedulesQuery(jobId.Value), ct)).Schedules;
        var target = live.FirstOrDefault(s => s.Name == scheduleName);
        return target is null ? null : new ScheduleTarget(jobId.Value, nowUtc, live, target);
    }
}

/// <summary>A resolved schedule: its slot job, the clock reading, the slot's live schedules, and the one named.</summary>
internal sealed record ScheduleTarget(long JobId, DateTime NowUtc, IReadOnlyList<LiveSchedule> Live, LiveSchedule Schedule)
{
    /// <summary>The slot cursor once <see cref="Schedule"/> changes as <paramref name="change"/> says: the MIN over the live set.</summary>
    public DateTime? SlotNextRunWith(Func<LiveSchedule, LiveSchedule> change) =>
        ScheduleWalker.RecomputeSlotMin([.. Live.Select(s => s.Id == Schedule.Id ? change(s) : s)], NowUtc);
}
