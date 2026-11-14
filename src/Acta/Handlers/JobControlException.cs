namespace Acta;

/// <summary>
/// Base for the non-error control-flow signals a handler raises to steer its own scheduling: the host catches the
/// base type and translates the concrete signal into a scheduler action (re-arm, suspend) without charging the
/// failure budget, and records <see cref="ReasonMessage"/> on the job's event. Do not catch it in user code: swallowing
/// it turns a deliberate reschedule, sleep, or termination into a silent fall-through. The family is closed: only Acta
/// derives from it, and only the <see cref="JobContext"/> control calls throw its public members,
/// <see cref="RescheduleJobException"/> and the <see cref="HandlerControlException"/> family.
/// </summary>
public abstract class JobControlException : ActaException
{
    private protected JobControlException(string? reasonMessage)
        : base(reasonMessage) => ReasonMessage = reasonMessage;

    /// <summary>Operator-readable reason, or <c>null</c> when none was given.</summary>
    public string? ReasonMessage { get; }
}
