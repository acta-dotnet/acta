namespace Acta;

/// <summary><see cref="IJobs.GetStatusAsync"/>: resolves the lookup, then reads the status alone.</summary>
internal sealed class GetJobStatusHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobStatusResponse?> HandleAsync(GetJobStatusQuery query, CancellationToken ct) =>
        await lookup.ResolveAsync(query.Job, ct) is { } jobId && await ReadAsync(jobId, ct) is { } status
            ? new GetJobStatusResponse(status)
            : null;

    /// <summary>The status by id, or null when no row matches.</summary>
    internal async ValueTask<JobStatusCode?> ReadAsync(long jobId, CancellationToken ct) =>
        await session.QueryAsync<JobStatusCode?>(
            "GetJobStatus",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId)),
            static async (reader, token) =>
                await reader.ReadAsync(token) ? (JobStatusCode)reader.GetByteFromNumeric(0) : (JobStatusCode?)null,
            ct
        );
}
