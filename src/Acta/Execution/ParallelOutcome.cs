namespace Acta;

/// <summary>
/// Result of <c>JobContext.ParallelAsync</c>: the branch outcomes keyed by branch name. The
/// group waits for every branch and returns; it never throws because a branch failed. Call
/// <see cref="ThrowIfAnyFailed"/> to opt into escalation.
/// </summary>
public sealed record ParallelOutcome(string GroupName, IReadOnlyDictionary<string, ChildJobOutcome> Branches)
{
    /// <summary>
    /// The outcome of the named branch; throws <see cref="KeyNotFoundException"/> for an unknown name.
    /// </summary>
    public ChildJobOutcome this[string branchName] => Branches[branchName];

    /// <summary>
    /// True when the group deadline passed before at least one branch landed terminal. Always false
    /// for the unbounded overload, which waits until every branch lands.
    /// </summary>
    public bool TimedOut => Branches.Values.Any(o => o.TimedOut);

    /// <summary>True when every branch landed Succeeded (in time, for the bounded overload).</summary>
    public bool Succeeded => Branches.Values.All(o => o.Succeeded);

    /// <summary>The non-succeeded branches (failed, cancelled, or timed out), keyed by branch name.</summary>
    public IReadOnlyDictionary<string, ChildJobOutcome> Failed =>
        Branches.Where(kv => !kv.Value.Succeeded).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

    /// <summary>
    /// Throws <see cref="ChildGroupException"/> carrying the failed branches when any branch did not
    /// land Succeeded; otherwise returns.
    /// </summary>
    public void ThrowIfAnyFailed()
    {
        var failed = Branches.Values.Where(o => !o.Succeeded).ToArray();
        if (failed.Length > 0)
        {
            throw new ChildGroupException(failed);
        }
    }
}
