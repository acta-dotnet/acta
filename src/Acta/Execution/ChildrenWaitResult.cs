namespace Acta;

/// <summary>
/// Result of a bounded group wait (<c>ctx.TryWaitChildrenAsync</c>, <c>ctx.WaitChildrenAsync</c> with a
/// timeout, and the bounded <c>ctx.JoinAsync</c>): one entry per awaited child, in caller order. A child
/// that landed before the group deadline carries its terminal outcome; one that did not carries
/// <see cref="ChildJobOutcome.TimedOut"/>. Returned, never thrown; call <see cref="ThrowIfAnyFailed"/>
/// to opt into escalation.
/// </summary>
/// <remarks>
/// The whole group shares one persisted deadline, so the wait cannot outlive it however many children
/// it holds or however often the handler replays. Only the unfinished children are cancelled on
/// expiry; the awaiting Job is never cancelled by a group timeout. Construction is gated to the
/// framework (private constructor, internal factory).
/// </remarks>
public sealed record ChildrenWaitResult
{
    private ChildrenWaitResult(IReadOnlyList<ChildJobOutcome> children) => Children = children;

    /// <summary>The per-child entries, in the order the child ids were given.</summary>
    public IReadOnlyList<ChildJobOutcome> Children { get; }

    /// <summary>True when the group deadline passed before at least one child landed terminal.</summary>
    public bool TimedOut => Children.Any(o => o.TimedOut);

    /// <summary>True when every child landed Succeeded in time.</summary>
    public bool Succeeded => Children.All(o => o.Succeeded);

    /// <summary>The non-succeeded children (failed, cancelled, or timed out), in caller order.</summary>
    public IReadOnlyList<ChildJobOutcome> Failed => Children.Where(o => !o.Succeeded).ToArray();

    /// <summary>
    /// Throws <see cref="ChildGroupException"/> carrying the non-succeeded children when any child did
    /// not land Succeeded in time; otherwise returns. A timed-out child counts as not-succeeded and the
    /// exception message names it.
    /// </summary>
    public void ThrowIfAnyFailed()
    {
        var failed = Failed;
        if (failed.Count > 0)
        {
            throw new ChildGroupException(failed);
        }
    }

    internal static ChildrenWaitResult From(IReadOnlyList<ChildJobOutcome> children) => new(children);
}
