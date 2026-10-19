namespace Acta;

/// <summary>
/// One mapped item's result: the original key, the child job id it was fanned out to, and the
/// child's terminal outcome.
/// </summary>
public sealed record MapItemOutcome<TKey>(TKey Key, long ChildJobId, ChildJobOutcome Outcome)
    where TKey : notnull;
