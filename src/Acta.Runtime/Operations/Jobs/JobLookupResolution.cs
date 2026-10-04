namespace Acta.Runtime;

/// <summary>
/// The job id a <see cref="JobLookup"/> names: an id passes through, a ref and a deduplication key resolve
/// (canonicalized first). Null when no job matches.
/// </summary>
internal sealed class JobLookupResolution(ResolveJobIdByRefHandler byRef, ResolveJobIdByDeduplicationKeyHandler byDeduplicationKey)
{
    public ValueTask<long?> ResolveAsync(JobLookup job, CancellationToken ct) =>
        job.Kind switch
        {
            JobLookupKind.JobId => ValueTask.FromResult<long?>(job.JobId),
            JobLookupKind.JobRef => byRef.HandleAsync(new ResolveJobIdByRefQuery(job.JobRef!.Value), ct),
            JobLookupKind.DeduplicationKey => byDeduplicationKey.HandleAsync(
                new ResolveJobIdByDeduplicationKeyQuery(
                    IdentifierSyntax.CanonicalizeKebab(job.JobNamespace!, nameof(job.JobNamespace)),
                    IdentifierSyntax.NormalizeKeyLookup(job.DeduplicationKey!, nameof(job.DeduplicationKey))
                ),
                ct
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(job), job.Kind, "Unsupported job lookup kind."),
        };
}
