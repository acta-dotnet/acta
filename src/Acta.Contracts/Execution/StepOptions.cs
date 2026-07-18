namespace Acta;

/// <summary>
/// Immutable snapshot of the per-step retry overrides supplied through the
/// <c>ctx.RunStepAsync</c> <c>configure</c> callback. Every retry field is nullable: a <c>null</c> field
/// inherits the parent <c>[Job]</c> policy, resolved live each attempt. <see cref="AtMostOnce"/> is a
/// distinct execution-semantics switch, not a retry override: when set the step body runs zero or one
/// times (never retried), so it is incompatible with every retry field except <c>MaxAttempts(1)</c>.
/// The framework never persists this snapshot; re-running the handler re-resolves it.
/// </summary>
public sealed record StepOptions(
    int? MaxAttempts,
    int? BackoffInitialDelaySeconds,
    int? BackoffMaxDelaySeconds,
    decimal? BackoffMultiplier,
    decimal? BackoffJitter,
    int? RetryWindowSeconds,
    bool AtMostOnce = false
)
{
    /// <summary>No overrides; the step inherits the parent <c>[Job]</c> policy entirely.</summary>
    public static readonly StepOptions Inherit = new(null, null, null, null, null, null);
}
