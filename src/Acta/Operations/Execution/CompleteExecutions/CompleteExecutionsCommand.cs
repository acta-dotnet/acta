namespace Acta;

/// <summary>
/// A batch of plain terminal completions by one worker: one-shot attempts landing Succeeded or Failed, with no
/// recurring fire, re-arm, suspend, signal, handler status, or deadline cancel, written in one transaction.
/// </summary>
internal sealed record CompleteExecutionsCommand(int WorkerId, IReadOnlyList<CompleteExecutionsValues> Completions);

/// <summary>
/// One attempt's plain terminal completion. <c>Result</c> is written when <c>ResultFormatId</c> is not None;
/// <c>FailureCount</c> is the exhausted count on a terminal failure, null to keep the row's; <c>RetentionSeconds</c>
/// stamps the row's retention from the database clock, null to leave it.
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
);
