namespace Acta;

/// <summary>
/// The schedule a control verb addresses: its slot job, the database clock the verb decides against, the slot's
/// live schedules, and the one named.
/// </summary>
internal sealed record ResolveScheduleTargetResponse(long JobId, DateTime NowUtc, IReadOnlyList<LiveSchedule> Live, LiveSchedule Schedule);
