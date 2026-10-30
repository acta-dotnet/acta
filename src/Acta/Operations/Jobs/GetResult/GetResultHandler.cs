namespace Acta;

/// <summary><see cref="IJobs.GetResultAsync(JobLookup, CancellationToken)"/>: resolves the lookup, then reads the stored result.</summary>
internal sealed class GetResultHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetResultResponse?> HandleAsync(GetResultQuery query, CancellationToken ct) =>
        await ReadAsync(query.Job, ct) is { } row ? new GetResultResponse(row.Payload) : null;

    /// <summary>The latest stored result of the job the lookup names, or null when there is no job or no result.</summary>
    internal async ValueTask<GetResultRow?> ReadAsync(JobLookup job, CancellationToken ct) =>
        await getJobId.HandleAsync(new GetJobIdQuery(job), ct) is { JobId: var jobId }
            ? await ReadAsync(jobId, executionNumber: null, ct)
            : null;

    /// <summary>
    /// The job's stored result, or null when there is none or <paramref name="executionNumber"/> did not store it. Every
    /// claim and restart clears the result, so a stored one belongs to the runtime's current execution number.
    /// </summary>
    internal Task<GetResultRow?> ReadAsync(long jobId, int? executionNumber, CancellationToken ct) =>
        session.QuerySingleAsync(
            "GetResult",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, executionNumber));
            },
            DbProjectionResolver.Resolve<GetResultRow>(),
            ct
        );
}
