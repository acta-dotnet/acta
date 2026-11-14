namespace Acta;

/// <summary>
/// Holds the current Job in <c>Paused</c> until an external resume. Thrown by <c>ctx.PauseAsync</c>;
/// the Job is not retried automatically, does not set <c>next_run_at_utc</c>, and the failure budget
/// is untouched. Resumes only through the existing <c>IJobs.ResumeAsync</c> path.
/// </summary>
public sealed class HandlerPauseException : HandlerControlException
{
    internal HandlerPauseException(string? reasonMessage)
        : base(reasonMessage) { }
}
