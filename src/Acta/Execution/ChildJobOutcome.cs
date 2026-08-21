namespace Acta;

/// <summary>
/// Terminal outcome of a child job, returned by <see cref="JobContext.WaitChildAsync"/>. Reports the
/// child's terminal status only; the wait never throws on a failed or cancelled child, the handler
/// branches on <see cref="Succeeded"/>, its typed result, or a business value. The failure reason is
/// not carried here: read it from the child's event timeline (<c>ListJobEventsAsync</c>).
/// </summary>
public sealed record ChildJobOutcome(long ChildJobId, JobStatusCode Status)
{
    /// <summary>
    /// True when a bounded group wait gave up on this child before it landed. <see cref="Status"/> then
    /// carries <c>Cancelled</c>, the status Acta drove the child to when the group deadline passed,
    /// rather than one observed through the child's outcome latch; branch on this flag first. Always
    /// false on an outcome that came back from the latch, which is every outcome an unbounded wait
    /// returns.
    /// </summary>
    public bool TimedOut { get; internal init; }

    /// <summary>True when the child landed <c>Succeeded</c>. False on a timed-out child.</summary>
    public bool Succeeded => !TimedOut && Status == JobStatusCode.Succeeded;

    // The driven status, not an observed one: the group timeout cancels the unfinished child before the
    // handler resumes, so Cancelled is what Acta made true. A child that landed terminal in the same
    // instant as the expiry keeps whatever status it actually reached, and the flag is what says this
    // entry does not report it.
    internal static ChildJobOutcome Expired(long childJobId) => new(childJobId, JobStatusCode.Cancelled) { TimedOut = true };
}
