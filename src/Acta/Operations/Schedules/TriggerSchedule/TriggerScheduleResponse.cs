namespace Acta;

/// <summary>
/// What firing a schedule once now did, and the schedule's state after the attempt. It works for both delivery modes and
/// never moves the schedule's cadence. Applied means a job was made ready, not that it ran. Calls are not deduplicated: a
/// retry after a lost response admits a second job on a per-occurrence schedule, while a permanent job rejects it until
/// it is idle again.
/// </summary>
/// <param name="Action">What the call did: applied; rejected, when the schedule is paused, its namespace is suspended, its permanent
/// job is not idle, or a per-occurrence schedule is at maxOutstanding; or not-found.</param>
/// <param name="Status">The schedule's status after the call; null when the schedule was not found.</param>
/// <param name="PausedUntilUtc">When the schedule's pause ends, in UTC; null when it is not paused or is paused until resumed.</param>
/// <param name="NextOccurrenceAtUtc">The schedule's next occurrence, in UTC; null when it has none.</param>
/// <param name="Version">The schedule's version after the call; null when the schedule was
/// not found.</param>
/// <param name="JobRef">The ref of the job the call fired: a new job on a per-occurrence schedule, the permanent job on a
/// permanent one; null unless the call was applied.</param>
public sealed record TriggerScheduleResponse(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextOccurrenceAtUtc,
    long? Version,
    JobRef? JobRef
) : IScheduleControlResponse;
