namespace Acta;

/// <summary>
/// Immutable snapshot of what the <c>ctx.RunStepAsync</c> <c>configure</c> callback chose. <see cref="AtMostOnce"/> is
/// the one option, an execution-semantics switch under which the body runs zero or one times; a step retries through
/// its job, so the retry policy is the parent <c>[Job]</c>'s. Construction is gated to <see cref="StepOptionsBuilder"/>;
/// a <c>RunStepCoreAsync</c> override receives an instance, and <c>Inherit with { ... }</c> covers the rest.
/// </summary>
public sealed record StepOptions
{
    internal StepOptions(bool atMostOnce)
    {
        AtMostOnce = atMostOnce;
    }

    public bool AtMostOnce { get; init; }

    /// <summary>No options: an ordinary step, retried through the parent <c>[Job]</c>'s policy.</summary>
    public static readonly StepOptions Inherit = new(atMostOnce: false);
}
