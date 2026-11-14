namespace Acta;

/// <summary>
/// Base for the non-error control-flow signals a handler raises to steer its own scheduling: the host catches the
/// base type and translates the concrete signal into a scheduler action (re-arm, suspend) without charging the
/// failure budget, and records <see cref="ReasonMessage"/> on the job's event. Let it through a catch: a handler that
/// swallows it and returns normally fails at once, with no retry. The family is closed: only Acta derives
/// from it, and only the <see cref="JobContext"/> control calls throw its public members,
/// <see cref="RescheduleJobException"/> and the <see cref="HandlerControlException"/> family.
/// </summary>
public abstract class JobControlException : ActaException
{
    private protected JobControlException(string? reasonMessage)
        : base(reasonMessage) => ReasonMessage = reasonMessage;

    /// <summary>Operator-readable reason, or <c>null</c> when none was given.</summary>
    public string? ReasonMessage { get; }
}
