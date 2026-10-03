namespace Acta;

/// <summary>What pausing a schedule did, and the schedule's state after the attempt.</summary>
public sealed record PauseScheduleResponse(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextRunAtUtc,
    int? Version
) : IScheduleControlResponse;
