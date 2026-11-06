namespace Acta;

/// <summary>
/// The start decision plus the step fields the orchestration needs: the attempt about to run, the
/// version for the completion CAS, the retry instant (Suspend), the stored result (ReplaySuccess), and
/// the final failure (Exhausted).
/// </summary>
[DbProjection]
internal sealed record StartJobStepResponse(
    StartJobStepOutcomeCode Outcome,
    int AttemptNumber,
    int Version,
    DateTime? NextRetryAtUtc,
    byte ResultFormatId,
    byte[]? Result,
    StepFailureCode? FailureCode,
    string? FailureMessage
);
