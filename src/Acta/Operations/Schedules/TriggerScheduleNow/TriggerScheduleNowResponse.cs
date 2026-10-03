namespace Acta;

/// <summary>What firing a schedule now did, and the schedule's state after the attempt.</summary>
public sealed record TriggerScheduleNowResponse(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextRunAtUtc,
    int? Version
) : IScheduleControlResponse;
