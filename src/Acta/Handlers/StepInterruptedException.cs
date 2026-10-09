namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync</c> when an <c>AtMostOnce</c> step slot is re-entered on replay after it was
/// durably started but never completed (the worker died mid-flight): the body is not re-invoked and the step row is
/// terminal <c>Interrupted</c>. The body may have run zero or one times, and Acta cannot tell which, since the start
/// marker and the external side effect cannot share a transaction; a handler that catches this must reconcile
/// against the external system by a stable reference, not blindly compensate. Left uncaught, the parent lands terminal
/// <c>Failed</c> at once (<c>job.step-interrupted</c>, no retry, budget untouched), never replayed into the same step.
/// </summary>
public sealed class StepInterruptedException(string stepName)
    : Exception(
        $"Step '{stepName}' was interrupted before its outcome was recorded. Because it was configured "
            + "AtMostOnce, the body will not be run again; it may have run zero or one times and must be "
            + "reconciled externally."
    )
{
    /// <summary>The step slot name that was interrupted.</summary>
    public string StepName { get; } = stepName;
}
