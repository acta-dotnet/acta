namespace Acta;

/// <summary>How one attempt ended, as the admission gates or the handler leave it for the completion write.</summary>
internal sealed record AttemptSettlement(CompletionKind Outcome)
{
    public EventReasonCode? Reason { get; init; }

    public string? ReasonMessage { get; init; }

    public byte ResultFormatId { get; init; }

    public ReadOnlyMemory<byte> Result { get; init; }

    public int? RescheduleDelaySeconds { get; init; }

    public DateTime? RescheduleResumeAtUtc { get; init; }

    public string? WaitSignalName { get; init; }

    public JobStatusCode? HandlerStatusCode { get; init; }

    public int DurationMs { get; init; }

    /// <summary>A deliberate terminal or hold: the target status overrides the outcome's, and the budget stays untouched.</summary>
    public static AttemptSettlement Terminal(CompletionKind outcome, EventReasonCode reason, string? message, JobStatusCode status) =>
        new(outcome)
        {
            Reason = reason,
            ReasonMessage = message.Truncate(ActaTextLimits.ReasonMessage),
            HandlerStatusCode = status,
        };
}
