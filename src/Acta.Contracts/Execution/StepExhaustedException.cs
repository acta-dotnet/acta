namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync</c> when the named step slot has exhausted its retry budget
/// (reached <c>MaxAttempts</c> or passed its <c>RetryWindow</c> without a successful invocation). The
/// step row is terminal <c>Exhausted</c>; the body is not invoked again.
/// </summary>
/// <remarks>
/// An ordinary exception, not a framework control signal: left uncaught it propagates out of the
/// handler and the parent attempt fails under the normal retry/failure rules. A handler may instead
/// catch it to run compensation and continue. On a parent replay the slot is already <c>Exhausted</c>,
/// so the next <c>RunStepAsync</c> call for the same name re-throws this immediately.
/// </remarks>
public sealed class StepExhaustedException : Exception
{
    /// <summary>
    /// Creates the exception for the exhausted step <paramref name="stepName"/> after
    /// <paramref name="attemptCount"/> attempt(s), carrying the final failure context.
    /// </summary>
    public StepExhaustedException(string stepName, int attemptCount, JobEventReasonCode? lastReasonCode, string? lastReasonMessage)
        : base(
            $"Step '{stepName}' exhausted after {attemptCount} attempt(s): {lastReasonMessage ?? lastReasonCode?.ToString() ?? "no reason recorded"}."
        )
    {
        StepName = stepName;
        AttemptCount = attemptCount;
        LastReasonCode = lastReasonCode;
        LastReasonMessage = lastReasonMessage;
    }

    /// <summary>The step slot name that exhausted.</summary>
    public string StepName { get; }

    /// <summary>The number of attempts made before exhaustion.</summary>
    public int AttemptCount { get; }

    /// <summary>Reason code of the final failed attempt, or <c>null</c> when none was recorded.</summary>
    public JobEventReasonCode? LastReasonCode { get; }

    /// <summary>Operator-readable message of the final failed attempt.</summary>
    public string? LastReasonMessage { get; }
}
