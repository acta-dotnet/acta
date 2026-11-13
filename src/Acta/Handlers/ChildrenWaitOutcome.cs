namespace Acta;

/// <summary>
/// Result of a group wait, <c>ctx.JoinAsync</c>: one entry per awaited child, in caller order. A child that landed
/// carries its terminal outcome; under a bounded wait, one that did not land before the group deadline carries
/// <see cref="JobOutcome.TimedOut"/>. Returned, never thrown; call <see cref="ThrowIfAnyFailed"/> to opt into
/// escalation. A bounded group shares one persisted deadline, so the wait cannot outlive it however many children it
/// holds or however often the handler replays; only the unfinished children are cancelled on expiry, never the
/// awaiting Job. Construction is gated to the framework.
/// </summary>
public sealed record ChildrenWaitOutcome
{
    private ChildrenWaitOutcome(IReadOnlyList<JobOutcome> children) => Children = children;

    /// <summary>The per-child entries, in the order the children were given.</summary>
    public IReadOnlyList<JobOutcome> Children { get; }

    /// <summary>True when the group deadline passed before at least one child landed terminal.</summary>
    public bool TimedOut => Children.Any(o => o.TimedOut);

    /// <summary>True when every child landed Succeeded in time.</summary>
    public bool Succeeded => Children.All(o => o.Succeeded);

    /// <summary>The non-succeeded children (failed, cancelled, or timed out), in caller order.</summary>
    public IReadOnlyList<JobOutcome> Failed => Children.Where(o => !o.Succeeded).ToArray();

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

    internal static ChildrenWaitOutcome From(IReadOnlyList<JobOutcome> children) => new(children);
}
