namespace Acta;

/// <summary>A batch of completions by one worker, of every shape, written in one transaction.</summary>
internal sealed record CompleteExecutionsCommand(int WorkerId, IReadOnlyList<CompleteExecutionsValues> Completions);

/// <summary>
/// One attempt's completion, landing Succeeded or Failed unless a shape field says otherwise, read in this order:
/// <c>WaitSignalName</c> suspends on that signal (Ready when it already arrived), <c>RescheduleStatusCode</c> re-arms
/// Ready, <c>HandlerStatusCode</c> lands what the handler asked for, and <c>FinalStatus</c> is a recurring fire, which
/// applies <c>ScheduleAdvances</c>, reads the slot's next run from its schedules, and keeps <c>RecurringResultCap</c>
/// results. <c>FailureCount</c> null keeps the row's; <c>RetentionSeconds</c> stamps retention at a terminal landing.
/// </summary>
internal sealed record CompleteExecutionsValues(
    long JobId,
    int ExecutionNumber,
    bool Succeeded,
    byte ResultFormatId,
    ReadOnlyMemory<byte> Result,
    EventReasonCode? EventReasonCode,
    string? ReasonMessage,
    int? DurationMs,
    int? FailureCount,
    int? RetentionSeconds
)
{
    public byte? RescheduleStatusCode { get; init; }

    public int? RescheduleDelaySeconds { get; init; }

    public DateTime? RescheduleResumeAtUtc { get; init; }

    public string? WaitSignalName { get; init; }

    public byte? HandlerStatusCode { get; init; }

    public JobStatusCode? FinalStatus { get; init; }

    public DateTime? JobNextRunAtUtc { get; init; }

    public int RecurringResultCap { get; init; }

    public IReadOnlyList<ScheduleAdvance>? ScheduleAdvances { get; init; }

    /// <summary>The job is in a lane, as its claim read it; lane_id never changes.</summary>
    public bool HasLane { get; init; }

    /// <summary>The job has a parent, as its claim read it; parent_id never changes.</summary>
    public bool HasParent { get; init; }

    /// <summary>A landing with no shape field, lane, or parent: the routine writes only its own row, result, and event.</summary>
    public bool IsPlain =>
        RescheduleStatusCode is null
        && HandlerStatusCode is null
        && FinalStatus is null
        && WaitSignalName is null
        && !HasLane
        && !HasParent;
}
