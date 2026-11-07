namespace Acta;

/// <summary>What firing a schedule now did, and the schedule's state after the attempt.</summary>
/// <param name="Action">What the call did: applied; rejected, when the schedule cannot fire now, as when it is paused or its permanent slot is busy; or not-found.</param>
/// <param name="Status">The schedule's status after the call; null when the schedule was not found.</param>
/// <param name="PausedUntilUtc">When the schedule's pause ends, in UTC; null when it is not paused or is paused until resumed.</param>
/// <param name="NextOccurrenceAtUtc">The schedule's next occurrence, in UTC; null when it has none.</param>
/// <param name="Version">The schedule's version after the call; null when the schedule was
/// not found.</param>
public sealed record TriggerScheduleResponse(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextOccurrenceAtUtc,
    int? Version
) : IScheduleControlResponse;
