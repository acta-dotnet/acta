namespace Acta;

/// <summary>
/// Result of <see cref="JobContext.JoinAsync"/>: the child outcomes in caller order. The join waits
/// for every child and returns; it never throws because a child failed. Call
/// <see cref="ThrowIfAnyFailed"/> to opt into escalation.
/// </summary>
public sealed record JoinOutcome(IReadOnlyList<ChildJobOutcome> Children)
{
    /// <summary>True when every child landed Succeeded.</summary>
    public bool Succeeded => Children.All(o => o.Succeeded);

    /// <summary>The non-succeeded child outcomes (failed or cancelled), in caller order.</summary>
    public IReadOnlyList<ChildJobOutcome> Failed => Children.Where(o => !o.Succeeded).ToArray();

    /// <summary>
    /// Throws <see cref="ChildGroupException"/> carrying the failed children when any child did not
    /// land Succeeded; otherwise returns. The caller's explicit require-all-succeeded escalation.
    /// </summary>
    public void ThrowIfAnyFailed()
    {
        var failed = Failed;
        if (failed.Count > 0)
        {
            throw new ChildGroupException(failed);
        }
    }
}
