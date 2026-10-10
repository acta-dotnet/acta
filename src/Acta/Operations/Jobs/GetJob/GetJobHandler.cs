namespace Acta;

/// <summary><see cref="IJobs.GetAsync"/>: resolves the lookup, then reads the full snapshot.</summary>
internal sealed class GetJobHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobResponse?> HandleAsync(GetJobQuery query, CancellationToken ct) =>
        await getJobId.HandleAsync(new GetJobIdQuery(query.Job), ct) is { JobId: var jobId } ? await ReadAsync(jobId, ct) : null;

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
