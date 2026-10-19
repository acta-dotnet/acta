namespace Acta;

/// <summary>
/// The completion decision plus the retry instant when the outcome is <c>RetryScheduled</c>.
/// </summary>
[DbProjection]
internal sealed record CompleteStepResponse(CompleteStepOutcomeCode Outcome, DateTime? NextRetryAtUtc);
