namespace Acta;

/// <summary>
/// One mapped item's result: the original key and the terminal outcome of the child it was fanned out to, whose
/// <see cref="JobOutcome.JobId"/> names that child.
/// </summary>
public record MapItemOutcome<TKey>(TKey Key, JobOutcome Outcome)
    where TKey : notnull;

/// <summary>A <see cref="MapItemOutcome{TKey}"/> whose child was a call of a job with a result. Built by the framework.</summary>
public sealed record MapItemOutcome<TKey, TResult> : MapItemOutcome<TKey>
    where TKey : notnull
    where TResult : notnull
{
    internal MapItemOutcome(TKey key, JobOutcome<TResult> outcome)
        : base(key, outcome) => Outcome = outcome;

    /// <summary>The terminal outcome of the item's child, with its typed result on success.</summary>
    public new JobOutcome<TResult> Outcome { get; }
}
