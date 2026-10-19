namespace Acta;

/// <summary>
/// Immutable snapshot of the per-step retry overrides supplied through the <c>ctx.RunStepAsync</c> <c>configure</c>
/// callback. Every retry field is nullable, and a null inherits the parent <c>[Job]</c> policy, resolved live each
/// attempt; <see cref="AtMostOnce"/> is an execution-semantics switch, not a retry override, under which the body runs
/// zero or one times and every retry field but <c>MaxAttempts(1)</c> is incompatible. Construction is gated to
/// <see cref="StepOptionsBuilder"/>, the one place that enforces the incompatibility, so no public constructor can
/// build the contradiction; a <c>RunStepCoreAsync</c> override receives an instance, and <c>Inherit with { ... }</c> covers the rest.
/// </summary>
public sealed record StepOptions
{
    internal StepOptions(
        int? maxAttempts,
        int? backoffInitialDelaySeconds,
        int? backoffMaxDelaySeconds,
        decimal? backoffMultiplier,
        decimal? backoffJitter,
        int? retryWindowSeconds,
        bool atMostOnce = false
    )
    {
        MaxAttempts = maxAttempts;
        BackoffInitialDelaySeconds = backoffInitialDelaySeconds;
        BackoffMaxDelaySeconds = backoffMaxDelaySeconds;
        BackoffMultiplier = backoffMultiplier;
        BackoffJitter = backoffJitter;
        RetryWindowSeconds = retryWindowSeconds;
        AtMostOnce = atMostOnce;
    }

    public int? MaxAttempts { get; init; }
    public int? BackoffInitialDelaySeconds { get; init; }
    public int? BackoffMaxDelaySeconds { get; init; }
    public decimal? BackoffMultiplier { get; init; }
    public decimal? BackoffJitter { get; init; }
    public int? RetryWindowSeconds { get; init; }
    public bool AtMostOnce { get; init; }

    /// <summary>No overrides; the step inherits the parent <c>[Job]</c> policy entirely.</summary>
    public static readonly StepOptions Inherit = new(null, null, null, null, null, null);
}
