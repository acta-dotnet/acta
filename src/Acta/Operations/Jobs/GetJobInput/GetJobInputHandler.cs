namespace Acta;

/// <summary><see cref="IJobs.GetInputAsync(JobLookup, CancellationToken)"/>: resolves the lookup, then reads the stored input.</summary>
internal sealed class GetJobInputHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobInputResponse?> HandleAsync(GetJobInputQuery query, CancellationToken ct) =>
        await ReadAsync(query.Job, ct) is { Format.IsNone: false } row ? new GetJobInputResponse(row.Payload) : null;

    /// <summary>The stored input of the job the lookup names, None format included, or null when no job matches.</summary>
    internal async ValueTask<GetJobInputRow?> ReadAsync(JobLookup job, CancellationToken ct) =>
        await getJobId.HandleAsync(new GetJobIdQuery(job), ct) is { JobId: var jobId }
            ? await session.QuerySingleAsync(
                "GetJobInput",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId)),
                DbProjectionResolver.Resolve<GetJobInputRow>(),
                ct
            )
            : null;
}
