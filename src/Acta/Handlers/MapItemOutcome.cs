namespace Acta;

/// <summary>
/// One mapped item's result: the original key and the terminal outcome of the child it was fanned out to, whose
/// <see cref="JobOutcome.JobId"/> names that child. Built by the framework.
/// </summary>
public record MapItemOutcome<TKey>
    where TKey : notnull
{
    internal MapItemOutcome(TKey key, JobOutcome outcome)
    {
        Key = key;
        Outcome = outcome;
    }

    /// <summary>The item's key, as the map's key selector gave it.</summary>
    public TKey Key { get; }

    /// <summary>The terminal outcome of the item's child.</summary>
    public JobOutcome Outcome { get; }

    /// <summary>The key and the outcome, for <c>var (key, outcome) = item</c>.</summary>
    public void Deconstruct(out TKey key, out JobOutcome outcome) => (key, outcome) = (Key, Outcome);
}

/// <summary>A <see cref="MapItemOutcome{TKey}"/> whose child was a call of a job with a result. Built by the framework.</summary>
public sealed record MapItemOutcome<TKey, TResult> : MapItemOutcome<TKey>
    where TKey : notnull
    where TResult : notnull
{
    internal MapItemOutcome(TKey key, JobOutcome<TResult> outcome)
        : base(key, outcome) => Outcome = outcome;

    /// <summary>The terminal outcome of the item's child, with its typed result on a success that stored one.</summary>
    public new JobOutcome<TResult> Outcome { get; }
}
