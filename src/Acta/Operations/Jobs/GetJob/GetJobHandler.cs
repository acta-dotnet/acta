namespace Acta;

/// <summary><see cref="IJobs.GetAsync"/>: resolves the lookup, then reads the full snapshot.</summary>
internal sealed class GetJobHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobResponse?> HandleAsync(GetJobQuery query, CancellationToken ct) =>
        await lookup.ResolveAsync(query.Job, ct) is { } jobId ? await ReadAsync(jobId, ct) : null;

    /// <summary>The snapshot by id, or null when no row matches; the engine reads it this way too.</summary>
    internal async ValueTask<GetJobResponse?> ReadAsync(long jobId, CancellationToken ct)
    {
        var read = DbProjectionResolver.Resolve<GetJobResponse>();
        return await session.QuerySingleAsync(
            "GetJob",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId)),
            reader =>
            {
                var job = read(reader);
                return job.DeduplicationKey is null
                    ? job
                    : job with
                    {
                        DeduplicationKey = PersistedDeduplicationKey.Normalize(job.DeduplicationKey),
                    };
            },
            ct
        );
    }
}
