namespace Acta;

/// <summary>
/// Inert identity for one registered job, carried by its <see cref="JobCall{TInput}"/>: the manifest type that declares
/// it and the job name. The namespace is not carried here; it is resolved at enqueue from the manifest's runtime binding.
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
/// The identity of a job that returns a <typeparamref name="TResult"/>, carried by its
/// <see cref="JobCall{TInput, TResult}"/>.
/// </summary>
public readonly record struct JobContract<TInput, TResult>(Type ManifestType, string JobName)
{
    /// <summary>
    /// Simple <c>ManifestType.Name/JobName</c> form for user-facing output; resolver errors use the
    /// full type name.
    /// </summary>
    public override string ToString() => $"{ManifestType.Name}/{JobName}";
}
