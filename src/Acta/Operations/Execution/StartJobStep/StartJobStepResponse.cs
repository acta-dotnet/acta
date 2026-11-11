namespace Acta;

/// <summary>
/// The start decision plus the step fields the orchestration needs: the version for the completion CAS, the stored
/// result (ReplaySuccess), and the body's failure (Exhausted).
/// </summary>
[DbProjection]
internal sealed record StartJobStepResponse(
    StartJobStepOutcomeCode Outcome,
    int Version,
    byte ResultFormatId,
    byte[]? Result,
    string? FailureMessage
);
