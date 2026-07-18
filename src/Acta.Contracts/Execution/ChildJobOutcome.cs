namespace Acta;

/// <summary>
/// Terminal outcome of a child job, returned by <see cref="JobContext.WaitChildAsync"/>. Reports the
/// child's terminal status only; the wait never throws on a failed or cancelled child, the handler
/// branches on <see cref="Succeeded"/>, its typed result, or a business value. The failure reason is
/// not carried here: read it from the child's event timeline (<c>ListJobEventsAsync</c>).
/// </summary>
public sealed record ChildJobOutcome(long ChildJobId, JobStatusCode Status)
{
    /// <summary>True when the child landed <c>Done</c>.</summary>
    public bool Succeeded => Status == JobStatusCode.Done;
}
