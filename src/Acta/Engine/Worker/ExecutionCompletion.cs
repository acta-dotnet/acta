namespace Acta;

/// <summary>How one execution ended, as the admission gates, the handler, or the executor leave it for the finalizer.</summary>
internal sealed record ExecutionCompletion(CompletionKind Outcome)
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
    public static ExecutionCompletion Terminal(CompletionKind outcome, EventReasonCode reason, string? message, JobStatusCode status) =>
        new(outcome)
        {
            Reason = reason,
            ReasonMessage = message.Truncate(ActaTextLimits.ReasonMessage),
            HandlerStatusCode = status,
        };
}
