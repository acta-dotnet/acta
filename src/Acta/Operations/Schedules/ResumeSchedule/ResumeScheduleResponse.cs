namespace Acta;

/// <summary>What resuming a schedule did, and the schedule's state after the attempt.</summary>
/// <param name="Action">What the call did, such as applied, already-in-state, rejected, not-found, or version-conflict.</param>
/// <param name="Status">The schedule's status after the call; null when the schedule was not found.</param>
/// <param name="PausedUntilUtc">When the schedule's pause ends, in UTC; null when it is not paused or is paused until resumed.</param>
/// <param name="NextOccurrenceAtUtc">The schedule's next occurrence, in UTC; null when it has none.</param>
/// <param name="Version">The schedule's version after the call; on a version conflict, the version it has now; null when the schedule was
/// not found.</param>
public sealed record ResumeScheduleResponse(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextOccurrenceAtUtc,
    int? Version
) : IScheduleControlResponse;
