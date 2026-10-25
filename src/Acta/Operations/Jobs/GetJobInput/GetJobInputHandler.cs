namespace Acta;

/// <summary><see cref="IJobs.GetInputAsync"/>: resolves the lookup, then reads the stored input.</summary>
internal sealed class GetJobInputHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetJobInputResponse?> HandleAsync(GetJobInputQuery query, CancellationToken ct)
    {
        if (await getJobId.HandleAsync(new GetJobIdQuery(query.Job), ct) is not { JobId: var jobId })
        {
            return null;
        }

        var record = await ReadAsync(jobId, ct);
        // A None-format row carries no input; surface it as null like a missing job.
        return record is null || record.FormatId == 0
            ? null
            : new GetJobInputResponse(JobPayload.Trusted(JobPayloadFormat.ForId(record.FormatId), record.Input ?? []));
    }

    /// <summary>The stored input payload and its format by id, or null when no row matches.</summary>
    internal Task<GetJobInputRow?> ReadAsync(long jobId, CancellationToken ct) =>
        session.QuerySingleAsync(
            "GetJobInput",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId)),
            DbProjectionResolver.Resolve<GetJobInputRow>(),
            ct
        );
}
