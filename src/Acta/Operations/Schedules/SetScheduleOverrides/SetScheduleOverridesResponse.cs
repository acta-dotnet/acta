namespace Acta;

/// <summary>What setting a schedule's overrides did, and the schedule's state after the attempt.</summary>
public sealed record SetScheduleOverridesResponse(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextOccurrenceAtUtc,
    int? Version
) : IScheduleControlResponse;
