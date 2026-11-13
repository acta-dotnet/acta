namespace Acta;

/// <summary>
/// One mapped item's result: the original key and the terminal outcome of the child it was fanned out to, whose
/// <see cref="JobOutcome.JobId"/> names that child.
/// </summary>
public sealed record MapItemOutcome<TKey>(TKey Key, JobOutcome Outcome)
    where TKey : notnull;
