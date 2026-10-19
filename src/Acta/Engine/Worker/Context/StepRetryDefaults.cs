namespace Acta;

/// <summary>
/// The parent Job's resolved retry policy, supplied to <see cref="RuntimeJobContext"/> so an inline
/// step can resolve its effective policy live each attempt (parent defaults + per-step
/// <c>configure</c> overrides). Never persisted; re-resolved on every replay.
/// </summary>
internal readonly record struct StepRetryDefaults(
    int MaxAttempts,
    int BackoffInitialDelaySeconds,
    int BackoffMaxDelaySeconds,
    decimal BackoffMultiplier,
    decimal BackoffJitter
);
