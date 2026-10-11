namespace Acta;

/// <summary>
/// A batch of plain terminal completions by one worker: one-shot attempts landing Succeeded or Failed, with no
/// recurring fire, re-arm, suspend, signal, handler status, or deadline cancel, written in one transaction.
/// </summary>
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

    /// <summary>The row for a planned per-job command.</summary>
    public static CompleteExecutionsValues From(CompleteExecutionCommand command) =>
        new(
            command.JobId,
            command.ExpectedExecutionNumber,
            command.Outcome == CompletionKind.Succeeded,
            command.ResultFormatId,
            command.Result,
            command.EventReasonCode,
            command.ReasonMessage,
            command.DurationMs,
            command.FailureCount,
            command.RetentionSeconds
        )
        {
            RescheduleStatusCode = command.RescheduleStatusCode,
            RescheduleDelaySeconds = command.RescheduleDelaySeconds,
            RescheduleResumeAtUtc = command.RescheduleResumeAtUtc,
            WaitSignalName = command.WaitSignalName,
            HandlerStatusCode = command.HandlerStatusCode,
            FinalStatus = command.FinalStatus,
            JobNextRunAtUtc = command.JobNextRunAtUtc,
            RecurringResultCap = command.RecurringResultCap,
            ScheduleAdvances = command.ScheduleAdvances,
        };
}
