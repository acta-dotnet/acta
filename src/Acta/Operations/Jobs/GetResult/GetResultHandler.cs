namespace Acta;

/// <summary><see cref="IJobs.GetResultAsync(JobLookup, CancellationToken)"/>: resolves the lookup, then reads the stored result.</summary>
internal sealed class GetResultHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetResultResponse?> HandleAsync(GetResultQuery query, CancellationToken ct)
    {
        if (await getJobId.HandleAsync(new GetJobIdQuery(query.Job), ct) is not { JobId: var jobId })
        {
            return null;
        }

        var record = await ReadAsync(jobId, executionNumber: null, ct);
        return record is null ? null : new GetResultResponse(JobPayload.FromBytes(record.Format, record.Data.ToArray()));
    }

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
