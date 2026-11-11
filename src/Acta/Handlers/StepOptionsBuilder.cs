namespace Acta;

/// <summary>
/// Option builder handed to the <c>ctx.RunStepAsync</c> <c>configure</c> callback. A step retries through its job, so
/// its retry policy is the parent <c>[Job]</c>'s <c>MaxAttempts</c> and <c>Backoff</c>, and <see cref="AtMostOnce"/> is
/// the one option. The framework constructs the builder, runs the callback, then snapshots it via <see cref="Build"/>;
/// callers never construct it or call <see cref="Build"/>.
/// </summary>
public sealed class StepOptionsBuilder
{
    private bool _atMostOnce;

    internal StepOptionsBuilder() { }

    /// <summary>
    /// Runs the body at most once, for effects where a double run is worse than none (charge a card). A throw is recorded
    /// <c>Failed</c> and surfaces as <see cref="StepFailedException"/> on every pass; a replay finding the step started
    /// but unfinished throws <see cref="StepInterruptedException"/>, and the handler reconciles externally. A wait, sleep,
    /// reschedule, pause, or child wait in the body throws <see cref="InvalidOperationException"/> at the call, and a
    /// reschedule or pause exception the body throws fails the step, since a resume would replay the body. Read from
    /// code on every pass, so switching it mid-flight reinterprets a started step.
    /// </summary>
    public StepOptionsBuilder AtMostOnce()
    {
        _atMostOnce = true;
        return this;
    }

    /// <summary>Snapshots the chosen options into an immutable <see cref="StepOptions"/>. Repeatable.</summary>
    internal StepOptions Build() => new(_atMostOnce);
}
