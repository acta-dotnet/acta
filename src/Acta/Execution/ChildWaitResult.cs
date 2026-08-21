namespace Acta;

/// <summary>
/// Outcome of a bounded <c>ctx.TryWaitChildAsync(childJobId, timeout)</c>: either the child reached a
/// terminal status in time (<see cref="Completed"/>, carrying its <see cref="Outcome"/>) or the wait's
/// stored expiration passed first (<see cref="TimedOut"/>). Returned, never thrown.
/// </summary>
/// <remarks>
/// A child timeout never cancels the parent: the parent resumes and may compensate, retry with a new
/// child, or cancel itself. Acta cancels the timed-out child and its descendant subtree before the
/// handler resumes. Construction is gated to the framework (private constructor, internal factories)
/// so a caller cannot synthesise a result that both completed and timed out.
/// </remarks>
public sealed record ChildWaitResult
{
    private ChildWaitResult(long childJobId, ChildJobOutcome? outcome)
    {
        ChildJobId = childJobId;
        Outcome = outcome;
    }

    /// <summary>The awaited child's job id, carried on a timed-out result too.</summary>
    public long ChildJobId { get; }

    /// <summary>True when the wait's expiration passed before the child landed terminal.</summary>
    public bool TimedOut => Outcome is null;

    /// <summary>True when the child landed terminal in time; the complement of <see cref="TimedOut"/>.</summary>
    public bool Completed => Outcome is not null;

    /// <summary>The child's terminal outcome, or <c>null</c> on a timeout.</summary>
    public ChildJobOutcome? Outcome { get; }

    internal static ChildWaitResult Expired(long childJobId) => new(childJobId, outcome: null);

    internal static ChildWaitResult Landed(ChildJobOutcome outcome) => new(outcome.ChildJobId, outcome);
}
