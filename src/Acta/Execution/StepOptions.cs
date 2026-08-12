namespace Acta;

/// <summary>
/// Immutable snapshot of the per-step retry overrides supplied through the
/// <c>ctx.RunStepAsync</c> <c>configure</c> callback. Every retry field is nullable: a <c>null</c> field
/// inherits the parent <c>[Job]</c> policy, resolved live each attempt. <see cref="AtMostOnce"/> is a
/// distinct execution-semantics switch, not a retry override: when set the step body runs zero or one
/// times (never retried), so it is incompatible with every retry field except <c>MaxAttempts(1)</c>.
/// The framework never persists this snapshot; re-running the handler re-resolves it.
/// </summary>
/// <remarks>
/// Construction is gated to <see cref="StepOptionsBuilder"/>, which is the only place the
/// <see cref="AtMostOnce"/>-versus-retry incompatibility is enforced. A public constructor would let a
/// caller build the exact contradictory combination the builder rejects, so there is not one; a
/// subclass overriding a <c>RunStepCore</c> sink receives an instance rather than making one, and
/// <c>Inherit with { ... }</c> covers the rest.
/// </remarks>
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
