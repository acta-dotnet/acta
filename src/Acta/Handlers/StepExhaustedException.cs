namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync</c> when the named step slot has exhausted its retry budget (reached
/// <c>MaxAttempts</c> or passed its <c>RetryWindow</c> without a successful invocation): the step row is terminal
/// <c>Exhausted</c> and the body is not invoked again. An ordinary exception, not a control signal: left uncaught it
/// fails the parent attempt under the normal retry rules, or a handler catches it to run compensation and continue.
/// On a parent replay the slot is already <c>Exhausted</c>, so the next call for the same name re-throws at once until
/// an operator restart gives the step a fresh budget. Carries the step name, the attempt count, and the final failure context.
/// </summary>
public sealed class StepExhaustedException(string stepName, int attemptCount, StepFailureCode? lastReasonCode, string? lastReasonMessage)
    : Exception(
        $"Step '{stepName}' exhausted after {attemptCount} attempt(s): {lastReasonMessage ?? lastReasonCode?.ToString() ?? "no reason recorded"}."
    )
{
    /// <summary>The step slot name that exhausted.</summary>
    public string StepName { get; } = stepName;

    /// <summary>The number of attempts made before exhaustion.</summary>
    public int AttemptCount { get; } = attemptCount;

    /// <summary>Reason code of the final failed attempt, or <c>null</c> when none was recorded.</summary>
    public StepFailureCode? LastReasonCode { get; } = lastReasonCode;

    /// <summary>Operator-readable message of the final failed attempt.</summary>
    public string? LastReasonMessage { get; } = lastReasonMessage;
}
