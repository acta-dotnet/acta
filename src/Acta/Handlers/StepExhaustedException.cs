namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync</c> when an <c>AtMostOnce</c> step's body threw: the step row is terminal
/// <c>Exhausted</c> and the body is not invoked again. An ordinary exception, not a control signal: left uncaught it
/// fails the job at once (<c>job.non-retryable-exception</c>), since every retry would replay the exhausted step, or a
/// handler catches it to run compensation and continue. On a replay the next call for the same name re-throws at once,
/// until an operator restart clears the step. Carries the step name and the body's failure.
/// </summary>
public sealed class StepExhaustedException(string stepName, StepFailureCode? lastReasonCode, string? lastReasonMessage)
    : Exception(
        $"At-most-once step '{stepName}' failed and does not run again: {lastReasonMessage ?? lastReasonCode?.ToString() ?? "no reason recorded"}."
    )
{
    /// <summary>The name of the step that exhausted.</summary>
    public string StepName { get; } = stepName;

    /// <summary>Reason code of the body's failure, or <c>null</c> when none was recorded.</summary>
    public StepFailureCode? LastReasonCode { get; } = lastReasonCode;

    /// <summary>Operator-readable message of the body's failure.</summary>
    public string? LastReasonMessage { get; } = lastReasonMessage;
}
