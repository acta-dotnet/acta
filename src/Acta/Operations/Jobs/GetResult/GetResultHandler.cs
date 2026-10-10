namespace Acta;

/// <summary><see cref="IJobs.GetResultAsync(JobLookup, CancellationToken)"/>: resolves the lookup, then reads the latest result.</summary>
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

    /// <summary>The stored result for the latest execution, or for <paramref name="executionNumber"/>; null when there is none.</summary>
    internal Task<GetResultRow?> ReadAsync(long jobId, int? executionNumber, CancellationToken ct) =>
        session.QuerySingleAsync(
            "GetResult",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Result.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Result.ExecutionNumber, executionNumber));
            },
            DbProjectionResolver.Resolve<GetResultRow>(),
            ct
        );
}
