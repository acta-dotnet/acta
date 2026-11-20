namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync</c> and <c>ctx.RunStepWithCheckAsync</c> when a step fails for good: an <c>AtMostOnce</c>
/// body threw, so its row is terminal <c>Failed</c> and the body is not invoked again; a step's result, from its body or
/// found by its check, could not be recorded (not serializable, too large to store); or a check step's body called a wait,
/// sleep, reschedule, pause or child wait. The last two leave the step started. An ordinary exception, not a control signal:
/// left uncaught it fails the job at once (<c>non-retryable-exception</c>), since a retry would throw again or run a finished
/// body twice, or a handler catches it to compensate and continue. A replay of a failed step re-throws until a restart clears it.
/// </summary>
public sealed class StepFailedException : ActaException
{
    public StepFailedException(string stepName, string? lastReasonMessage)
        : this(stepName, lastReasonMessage, innerException: null) { }

    internal StepFailedException(string stepName, string? lastReasonMessage, Exception? innerException)
        : base($"Step '{stepName}' failed for good: {lastReasonMessage ?? "no reason recorded"}.", innerException)
    {
        StepName = stepName;
        LastReasonMessage = lastReasonMessage;
    }

    /// <summary>The name of the step that failed.</summary>
    public string StepName { get; }

    /// <summary>Operator-readable message of the body's failure.</summary>
    public string? LastReasonMessage { get; }
}
