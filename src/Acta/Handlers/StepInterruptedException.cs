namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync</c> when an <c>AtMostOnce</c> step cannot know its outcome: it started and never
/// completed (the worker died mid-flight), or its body threw <see cref="OutcomeUnknownException"/>, itself or through
/// a step inside it. The body never runs again, and a re-entry marks the step terminal <c>Interrupted</c>. It ran zero
/// or one times, so a handler that catches this reconciles against the external system by a stable reference. Left
/// uncaught, the job lands terminal <c>Failed</c> at once (<c>step-interrupted</c>, no retry, budget untouched).
/// </summary>
public sealed class StepInterruptedException : ActaException
{
    private const string Reconcile =
        "Because it was configured AtMostOnce, the body will not be run again; it may have run zero or one times and must be "
        + "reconciled externally.";

    /// <summary>A step re-entered before its outcome was recorded.</summary>
    public StepInterruptedException(string stepName)
        : base($"Step '{stepName}' was interrupted before its outcome was recorded. {Reconcile}") => StepName = stepName;

    /// <summary>A step whose body could not tell whether its effect happened, itself or through a step inside it.</summary>
    internal StepInterruptedException(string stepName, Exception cause)
        : base($"Step '{stepName}' could not tell whether its effect happened: {Why(cause)}. {Reconcile}", cause) => StepName = stepName;

    private static string Why(Exception cause) =>
        cause is StepInterruptedException inner ? $"step '{inner.StepName}' inside it could not either" : cause.Message.TrimEnd('.');

    /// <summary>The name of the step that was interrupted.</summary>
    public string StepName { get; }
}
