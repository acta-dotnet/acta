namespace Acta;

/// <summary>
/// Validated <c>complete_step</c> inputs: the slot identity, the outcome and result payload, the audit
/// reason, and the C#-precomputed retry policy (jittered <c>DelaySeconds</c>, live <c>MaxAttempts</c>,
/// optional <c>RetryWindowSeconds</c>) plus the completion CAS token <c>ExpectedVersion</c>.
/// </summary>
internal sealed record CompleteStepCommand(
    long JobId,
    string Name,
    bool Succeeded,
    byte ResultFormatId,
    byte[]? Result,
    EventReasonCode? ReasonCode,
    string? ReasonMessage,
    int DelaySeconds,
    int MaxAttempts,
    int? RetryWindowSeconds,
    int ExpectedVersion
);
