namespace Acta;

/// <summary>
/// Thrown by a step body that cannot tell whether its effect happened, such as a payment call that timed out after the
/// request left. In an <c>AtMostOnce</c> step it ends the step the way a crash does: the step stays started, the call
/// throws <see cref="StepInterruptedException"/>, and no retry or restart runs the body again, so the handler reconciles
/// against the outside system by a stable reference. In any other step, and outside a step, it is an ordinary failure.
/// </summary>
public sealed class OutcomeUnknownException : ActaException
{
    /// <summary>An outcome the body cannot know, described by <paramref name="message"/>.</summary>
    public OutcomeUnknownException(string message)
        : base(message) { }

    /// <summary>An outcome the body cannot know, described by <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public OutcomeUnknownException(string message, Exception? innerException)
        : base(message, innerException) { }
}
