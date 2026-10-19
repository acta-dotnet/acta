namespace Acta;

/// <summary>
/// Base for the deliberate handler-control terminations <c>ctx.FailAsync</c>, <c>ctx.CancelAsync</c>, and
/// <c>ctx.PauseAsync</c>: each stops the current attempt with an intended target Status and an optional
/// operator-readable <paramref name="reasonMessage"/>, without charging the failure budget and without being recorded
/// as an unhandled exception. Unlike a thrown exception (a crash) these are intentional domain decisions: the host
/// catches the concrete type and routes it through <c>complete_executions</c> to the matching terminal or hold Status.
/// Throw via the <c>ctx</c> verbs rather than constructing these directly.
/// </summary>
public abstract class HandlerControlException(string? reasonMessage) : JobControlException(reasonMessage)
{
    /// <summary>Operator-readable reason, or <c>null</c> when the handler supplied none.</summary>
    public string? ReasonMessage { get; } = reasonMessage;
}
