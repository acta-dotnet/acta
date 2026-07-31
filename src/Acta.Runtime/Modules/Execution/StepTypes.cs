namespace Acta.Runtime.Modules.Execution;

internal enum StartExecutionAction : byte
{
    Started = 1,
    NotOwner = 2,
    LostClaim = 3,
    AlreadyTerminal = 4,
    LeaseExpired = 5,
}

/// <summary>
/// Routine outcome for one <c>start_step</c> call; mirrors the SQL <c>outcome_code</c>.
/// </summary>
internal enum StartStepOutcomeCode : byte
{
    /// <summary>Run the body for the attempt carried by <see cref="StartStepDecision.AttemptNumber"/>.</summary>
    Invoke = 1,

    /// <summary>Pending with an unreached retry instant; re-arm the parent until <see cref="StartStepDecision.NextRetryAtUtc"/>.</summary>
    Suspend = 2,

    /// <summary>Already succeeded; return the stored result without running the body.</summary>
    ReplaySuccess = 3,

    /// <summary>Already exhausted; throw <c>StepExhaustedException</c>.</summary>
    Exhausted = 4,

    /// <summary>An at-most-once step was re-entered before completion; the row was terminalized
    /// <c>Interrupted</c>. Do not run the body; throw <c>StepInterruptedException</c>.</summary>
    Interrupted = 5,
}

/// <summary>
/// The start decision plus the slot fields the orchestration needs: the attempt about to run, the
/// version for the completion CAS, the retry instant (Suspend), the stored result (ReplaySuccess), and
/// the final reason (Exhausted).
/// </summary>
internal sealed record StartStepDecision(
    StartStepOutcomeCode Outcome,
    short AttemptNumber,
    int Version,
    DateTime? NextRetryAtUtc,
    byte ResultFormatId,
    byte[]? Result,
    JobEventReasonCode? ReasonCode,
    string? ReasonMessage
);

/// <summary>
/// Routine outcome for one <c>complete_step</c> call; mirrors the SQL <c>outcome_code</c>.
/// </summary>
internal enum CompleteStepOutcomeCode : byte
{
    /// <summary>Body succeeded; the slot is terminal <c>Succeeded</c> with the stored result.</summary>
    Succeeded = 1,

    /// <summary>Body failed in budget; the slot stays <c>Pending</c> with <c>next_retry_at_utc</c> set.</summary>
    RetryScheduled = 2,

    /// <summary>Body failed and the budget is spent; the slot is terminal <c>Exhausted</c>.</summary>
    Exhausted = 3,

    /// <summary>
    /// The version CAS matched no row: the slot advanced under a concurrent execution of the same job,
    /// so this attempt no longer owns it. Nothing was written.
    /// </summary>
    StaleVersion = 4,
}

/// <summary>
/// The completion decision plus the retry instant when the outcome is <c>RetryScheduled</c>.
/// </summary>
internal sealed record CompleteStepDecision(CompleteStepOutcomeCode Outcome, DateTime? NextRetryAtUtc);

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
    JobEventReasonCode? ReasonCode,
    string? ReasonMessage,
    int DelaySeconds,
    short MaxAttempts,
    int? RetryWindowSeconds,
    int ExpectedVersion
);
