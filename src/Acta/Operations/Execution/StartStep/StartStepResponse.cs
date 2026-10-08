namespace Acta;

/// <summary>
/// Routine outcome for one <c>start_step</c> call; mirrors the SQL <c>outcome_code</c>.
/// </summary>
internal enum StartStepOutcomeCode : byte
{
    /// <summary>Run the body for the attempt carried by <see cref="StartStepResponse.AttemptNumber"/>.</summary>
    Invoke = 1,

    /// <summary>Pending with an unreached retry instant; re-arm the parent until <see cref="StartStepResponse.NextRetryAtUtc"/>.</summary>
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
