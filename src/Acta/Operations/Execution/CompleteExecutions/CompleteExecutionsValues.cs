namespace Acta;

/// <summary>
/// One attempt's completion, landing Succeeded or Failed unless a shape field says otherwise, read in this order:
/// <c>WaitSignalName</c> suspends on that signal (Ready when it already arrived), <c>RescheduleEventCode</c> re-arms
/// Ready and names the event a re-arm records at Audit, <c>HandlerStatusCode</c> lands what the handler asked for, and
/// <c>FinalStatus</c> is a recurring fire. <c>EventCode</c> is the attempt's own event, 40 to 44, with <c>Reason</c>
/// its <see cref="EventReason"/>. <c>FailureCount</c> null keeps the row's; <c>RetentionSeconds</c> stamps a terminal landing.
/// </summary>
internal sealed record CompleteExecutionsValues(
    long JobId,
    int ExecutionNumber,
    bool Succeeded,
    byte ResultFormatId,
    ReadOnlyMemory<byte> Result,
    EventCode EventCode,
    string? Reason,
    string? EventMessage,
    int? DurationMs,
    int? FailureCount,
    int? RetentionSeconds
)
{
    public EventCode? RescheduleEventCode { get; init; }

    public int? RescheduleDelaySeconds { get; init; }

    public DateTime? RescheduleResumeAtUtc { get; init; }

    public string? WaitSignalName { get; init; }

    public byte? HandlerStatusCode { get; init; }

    public JobStatusCode? FinalStatus { get; init; }

    public DateTime? JobNextRunAtUtc { get; init; }

    /// <summary>The job is in a lane, as its claim read it; lane_id never changes.</summary>
    public bool HasLane { get; init; }

    /// <summary>The job has a parent, as its claim read it; parent_job_id never changes.</summary>
    public bool HasParent { get; init; }

    /// <summary>The job is a per-occurrence root charged to a schedule, as its claim read it; schedule_id never changes.</summary>
    public bool HasSchedule { get; init; }

    /// <summary>A landing with no shape field, lane, parent, or schedule: the routine writes only its own row, result, and event.</summary>
    public bool IsPlain =>
        RescheduleEventCode is null
        && HandlerStatusCode is null
        && FinalStatus is null
        && WaitSignalName is null
        && !HasLane
        && !HasParent
        && !HasSchedule;
}
