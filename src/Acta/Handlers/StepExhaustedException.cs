namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync</c> when a step fails for good: an <c>AtMostOnce</c> body threw, so its row is terminal
/// <c>Exhausted</c> and the body is not invoked again, or any step's body finished and its result could not be recorded
/// (null, not serializable, over the inline cap), which leaves the step started. An ordinary exception, not a control
/// signal: left uncaught it fails the job at once (<c>non-retryable-exception</c>), since a retry would throw again or
/// run a finished body twice, or a handler catches it to run compensation and continue. A replay of an exhausted step
/// re-throws at once, until an operator restart clears it. Carries the step name and the failure.
/// </summary>
public sealed class StepExhaustedException : Exception
{
    public StepExhaustedException(string stepName, string? lastReasonMessage)
        : this(stepName, lastReasonMessage, innerException: null) { }

    internal StepExhaustedException(string stepName, string? lastReasonMessage, Exception? innerException)
        : base($"Step '{stepName}' failed for good: {lastReasonMessage ?? "no reason recorded"}.", innerException)
    {
        StepName = stepName;
        LastReasonMessage = lastReasonMessage;
    }

    /// <summary>The name of the step that exhausted.</summary>
    public string StepName { get; }

    /// <summary>Operator-readable message of the body's failure.</summary>
    public string? LastReasonMessage { get; }
}
