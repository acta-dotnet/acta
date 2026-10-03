namespace Acta;

/// <summary>
/// Generated, inert identity for one registered job: the manifest type that declares it and the
/// job name. Passed to the contract <c>EnqueueAsync</c>/<c>RunAndWaitAsync</c> overloads on
/// <see cref="IJobs"/> to name the enqueue target explicitly instead of inferring it from the
/// input type. The namespace is not carried here; it is resolved at enqueue from the manifest's
/// runtime binding.
/// </summary>
public readonly record struct JobContract<TInput>(Type ManifestType, string JobName)
{
    /// <summary>
    /// Simple <c>ManifestType.Name/JobName</c> form for user-facing output; resolver errors use the
    /// full type name.
    /// </summary>
    public override string ToString() => $"{ManifestType.Name}/{JobName}";
}

/// <summary>
/// Result-bearing contract for jobs that return a payload. Converts implicitly to
/// <see cref="JobContract{TInput}"/> so one generated member drives both the waiting
/// <c>RunAndWaitAsync</c> and fire-and-forget <c>EnqueueAsync</c> (which drops the result).
/// </summary>
public readonly record struct JobContract<TInput, TResult>(Type ManifestType, string JobName)
{
    /// <summary>
    /// Drops the result type, yielding the input-only contract for fire-and-forget enqueue.
    /// </summary>
    public static implicit operator JobContract<TInput>(JobContract<TInput, TResult> contract) =>
        new(contract.ManifestType, contract.JobName);

    /// <summary>
    /// Simple <c>ManifestType.Name/JobName</c> form for user-facing output; resolver errors use the
    /// full type name.
    /// </summary>
    public override string ToString() => $"{ManifestType.Name}/{JobName}";
}
