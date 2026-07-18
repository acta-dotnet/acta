namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync</c> when an <c>AtMostOnce</c> step slot is re-entered on replay
/// after it was durably started but never completed (the worker died mid-flight). The body is not
/// re-invoked and the step row is terminal <c>Interrupted</c>.
/// </summary>
/// <remarks>
/// The body may have run <b>zero or one times</b>; Acta cannot determine which, because the durable
/// start marker and the external side effect cannot share a transaction. This is not a signal that the
/// side effect happened, so a handler that catches this must reconcile against the external system by a
/// stable, recomputable reference, not blindly compensate. An ordinary exception, not a control signal:
/// left uncaught the parent Job lands terminal <c>Failed</c> at once (reason <c>job.step-interrupted</c>,
/// no retry, budget untouched), never replayed into the same interrupted step; caught, the handler owns
/// the recovery. On a subsequent replay the slot is already <c>Interrupted</c>, so re-entry re-throws.
/// </remarks>
public sealed class StepInterruptedException : Exception
{
    /// <summary>
    /// Creates the exception for the interrupted at-most-once step <paramref name="stepName"/>.
    /// </summary>
    public StepInterruptedException(string stepName)
        : base(
            $"Step '{stepName}' was interrupted before its outcome was recorded. Because it was configured "
                + "AtMostOnce, the body will not be run again; it may have run zero or one times and must be "
                + "reconciled externally."
        )
    {
        StepName = stepName;
    }

    /// <summary>The step slot name that was interrupted.</summary>
    public string StepName { get; }
}
