namespace Acta.Runtime;

/// <summary><see cref="IJobs.GetCheckpointsAsync"/>: resolves the lookup, then reads every slot; empty for a missing job.</summary>
internal sealed class GetJobCheckpointsHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<IReadOnlyList<JobCheckpointItem>> HandleAsync(GetJobCheckpointsQuery query, CancellationToken ct) =>
        await lookup.ResolveAsync(query.Job, ct) is { } jobId ? await ReadAsync(jobId, ct) : [];

    /// <summary>Every checkpoint slot by job id, ordered by kind then name; empty when the job has none.</summary>
    internal Task<IReadOnlyList<JobCheckpointItem>> ReadAsync(long jobId, CancellationToken ct)
    {
        var read = DbProjectionResolver.Resolve<JobCheckpointReadRow>();
        return session.QueryListAsync(
            "GetJobCheckpoints",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, jobId)),
            reader => read(reader).ToItem(),
            ct
        );
    }
}
