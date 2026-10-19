namespace Acta;

/// <summary>
/// The start decision plus the slot fields the orchestration needs: the attempt about to run, the
/// version for the completion CAS, the retry instant (Suspend), the stored result (ReplaySuccess), and
/// the final reason (Exhausted).
/// </summary>
[DbProjection]
internal sealed record StartStepResponse(
    StartStepOutcomeCode Outcome,
    int AttemptNumber,
    int Version,
    DateTime? NextRetryAtUtc,
    byte ResultFormatId,
    byte[]? Result,
    EventReasonCode? ReasonCode,
    string? ReasonMessage
);
