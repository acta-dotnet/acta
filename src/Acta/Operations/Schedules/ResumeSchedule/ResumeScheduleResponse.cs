namespace Acta;

/// <summary>What resuming a schedule did, and the schedule's state after the attempt.</summary>
public sealed record ResumeScheduleResponse(
    ControlAction Action,
    ScheduleStatusCode? Status,
    DateTime? PausedUntilUtc,
    DateTime? NextOccurrenceAtUtc,
    int? Version
) : IScheduleControlResponse;
