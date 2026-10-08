namespace Acta;

/// <summary>
/// Base for the non-error control-flow signals a handler raises to steer its own scheduling: the host catches the
/// base type and translates the concrete signal into a scheduler action (re-arm, suspend) without charging the
/// failure budget, and <paramref name="message"/> flows into the re-arm reason. Do not catch it in user code:
/// swallowing it turns a deliberate reschedule, sleep, or termination into a silent fall-through. The family is
/// closed to the framework; its public members are <see cref="RescheduleJobException"/> and the
/// <see cref="HandlerControlException"/> family.
/// </summary>
public abstract class JobControlException(string? message) : Exception(message) { }

/// <summary>
/// Re-arms the current Job to <c>Ready</c> with a forward-dated <c>NextRunAtUtc</c> and stops the attempt without
/// charging the failure budget; throw it directly, or call <c>ctx.RescheduleAsync</c> / <c>ctx.RescheduleUntilAsync</c>,
/// which throw it for you. Exactly one of <see cref="Delay"/> and <see cref="ResumeAtUtc"/> is set, and the host
/// computes the due instant in <c>complete_execution</c> from DB UTC time: a delay becomes <c>db_now + delay</c>, an
/// absolute instant is used verbatim, and a past one re-arms as immediately claimable.
/// </summary>
public sealed class RescheduleJobException : JobControlException
{
    /// <summary>
    /// Re-arms after a relative <paramref name="delay"/> (normalized to whole seconds; sub-second
    /// rounds up, zero re-arms immediately).
    /// </summary>
    public RescheduleJobException(TimeSpan delay, string? message = null)
        : base(message)
    {
        Delay = TimeSpan.FromSeconds(DurationSyntax.ToWholeSeconds(delay, nameof(delay)));
    }

    /// <summary>
    /// Re-arms at an absolute <paramref name="resumeAtUtc"/> (normalized to UTC).
    /// </summary>
    public RescheduleJobException(DateTimeOffset resumeAtUtc, string? message = null)
        : base(message)
    {
        ResumeAtUtc = resumeAtUtc.ToUniversalTime();
    }

    /// <summary>Relative re-arm delay, or <c>null</c> when an absolute instant was supplied.</summary>
    public TimeSpan? Delay { get; }

    /// <summary>Absolute re-arm instant (UTC), or <c>null</c> when a relative delay was supplied.</summary>
    public DateTimeOffset? ResumeAtUtc { get; }
}

/// <summary>
/// Base for the deliberate handler-control terminations <c>ctx.FailAsync</c>, <c>ctx.CancelAsync</c>, and
/// <c>ctx.PauseAsync</c>: each stops the current attempt with an intended target Status and an optional
/// operator-readable <paramref name="reasonMessage"/>, without charging the failure budget and without being recorded
/// as an unhandled exception. Unlike a thrown exception (a crash) these are intentional domain decisions: the host
/// catches the concrete type and routes it through <c>complete_execution</c> to the matching terminal or hold Status.
/// Throw via the <c>ctx</c> verbs rather than constructing these directly.
/// </summary>
public abstract class HandlerControlException(string? reasonMessage) : JobControlException(reasonMessage)
{
    /// <summary>Operator-readable reason, or <c>null</c> when the handler supplied none.</summary>
    public string? ReasonMessage { get; } = reasonMessage;
}

/// <summary>
/// Ends the current Job as a deliberate terminal <c>Failed</c>. Thrown by <c>ctx.FailAsync</c>; the
/// attempt is not retried and the failure budget is untouched. Distinct from a thrown exception, which
/// is recorded as an unhandled failure.
/// </summary>
/// <remarks>Initializes the fail control with an optional <paramref name="reasonMessage"/>.</remarks>
public sealed class HandlerFailException(string? reasonMessage = null) : HandlerControlException(reasonMessage) { }

/// <summary>
/// Ends the current Job as a deliberate terminal <c>Cancelled</c> (a non-failure stop). Thrown by
/// <c>ctx.CancelAsync</c>; the attempt is not retried and the failure budget is untouched.
/// </summary>
/// <remarks>Initializes the cancel control with an optional <paramref name="reasonMessage"/>.</remarks>
public sealed class HandlerCancelException(string? reasonMessage = null) : HandlerControlException(reasonMessage) { }

/// <summary>
/// Holds the current Job in <c>Paused</c> until an external resume. Thrown by <c>ctx.PauseAsync</c>;
/// the Job is not retried automatically, does not set <c>next_run_at_utc</c>, and the failure budget
/// is untouched. Resumes only through the existing <c>IJobs.ResumeAsync</c> path.
/// </summary>
/// <remarks>Initializes the pause control with an optional <paramref name="reasonMessage"/>.</remarks>
public sealed class HandlerPauseException(string? reasonMessage = null) : HandlerControlException(reasonMessage) { }
