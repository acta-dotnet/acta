namespace Acta;

/// <summary>
/// Result of <c>JobContext.MapAsync</c>: one <see cref="MapItemOutcome{TKey}"/> per input
/// item, in input order. The map waits for every child and returns; it never throws because a child
/// failed. Call <see cref="ThrowIfAnyFailed"/> to opt into escalation.
/// </summary>
public sealed record MapOutcome<TKey>(string GroupName, IReadOnlyList<MapItemOutcome<TKey>> Items)
    where TKey : notnull
{
    /// <summary>
    /// True when the group deadline passed before at least one item's child landed terminal. Always
    /// false for the unbounded overload, which waits until every child lands.
    /// </summary>
    public bool TimedOut => Items.Any(i => i.Outcome.TimedOut);

    /// <summary>True when every item's child landed Succeeded (in time, for the bounded overload).</summary>
    public bool Succeeded => Items.All(i => i.Outcome.Succeeded);

    /// <summary>The items whose child did not succeed (failed, cancelled, or timed out), in input order.</summary>
    public IReadOnlyList<MapItemOutcome<TKey>> Failed => Items.Where(i => !i.Outcome.Succeeded).ToArray();

    /// <summary>
    /// Throws <see cref="ChildGroupException"/> carrying the failed children when any item did not
    /// land Succeeded; otherwise returns.
    /// </summary>
    public void ThrowIfAnyFailed()
    {
        var failed = Items.Where(i => !i.Outcome.Succeeded).Select(i => i.Outcome).ToArray();
        if (failed.Length > 0)
        {
            throw new ChildGroupException(failed);
        }
    }
}

/// <summary>
/// One mapped item's result: the original key, the child job id it was fanned out to, and the
/// child's terminal outcome.
/// </summary>
public sealed record MapItemOutcome<TKey>(TKey Key, long ChildJobId, ChildJobOutcome Outcome)
    where TKey : notnull;
