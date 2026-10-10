namespace Acta;

/// <summary>
/// <see cref="IJobs.GetJobIdAsync"/>: an id passes through without a round trip, and a ref or a deduplication key
/// resolves (canonicalized first); no SQL of its own. Null when no job matches.
/// </summary>
internal sealed class GetJobIdHandler(ResolveJobIdByRefHandler byRef, ResolveJobIdByDeduplicationKeyHandler byDeduplicationKey)
{
    public async ValueTask<GetJobIdResponse?> HandleAsync(GetJobIdQuery query, CancellationToken ct) =>
        await ResolveAsync(query.Job, ct) is { } jobId ? new GetJobIdResponse(jobId) : null;

    private ValueTask<long?> ResolveAsync(JobLookup job, CancellationToken ct) =>
        job.Kind switch
        {
            JobLookupKind.JobId => ValueTask.FromResult<long?>(job.JobId),
            JobLookupKind.JobRef => byRef.HandleAsync(new ResolveJobIdByRefQuery(job.JobRef!.Value), ct),
            JobLookupKind.DeduplicationKey => byDeduplicationKey.HandleAsync(
                new ResolveJobIdByDeduplicationKeyQuery(
                    NameSyntax.Canonicalize(job.JobNamespace!, nameof(job.JobNamespace)),
                    KeySyntax.Canonicalize(job.DeduplicationKey!, nameof(job.DeduplicationKey))
                ),
                ct
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(job), job.Kind, "Unsupported job lookup kind."),
        };
}
