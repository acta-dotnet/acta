namespace Acta;

/// <summary>A resolved schedule: its slot job, the clock reading, the slot's live schedules, and the one named.</summary>
internal sealed record ResolveScheduleTargetResponse(long JobId, DateTime NowUtc, IReadOnlyList<LiveSchedule> Live, LiveSchedule Schedule)
{
    /// <summary>The slot cursor once <see cref="Schedule"/> changes as <paramref name="change"/> says: the MIN over the live set.</summary>
    public DateTime? SlotNextRunWith(Func<LiveSchedule, LiveSchedule> change) =>
        ScheduleWalker.RecomputeSlotMin([.. Live.Select(s => s.ScheduleName == Schedule.ScheduleName ? change(s) : s)], NowUtc);
}
