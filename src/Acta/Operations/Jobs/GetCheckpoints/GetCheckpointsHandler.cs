namespace Acta;

/// <summary><see cref="IJobs.GetCheckpointsAsync"/>: resolves the lookup, then reads every slot; null for a missing job.</summary>
internal sealed class GetCheckpointsHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetCheckpointsResponse?> HandleAsync(GetCheckpointsQuery query, CancellationToken ct) =>
        await lookup.ResolveAsync(query.Job, ct) is { } jobId ? await ReadAsync(jobId, ct) : null;

    /// <summary>Every checkpoint slot by job id, by kind and then name; empty when the job has none, null when no job has the id.</summary>
    internal Task<GetCheckpointsResponse?> ReadAsync(long jobId, CancellationToken ct)
    {
        var read = DbProjectionResolver.Resolve<GetCheckpointsRow>();
        // The SQL answers one row with a null kind for a job without slots, and none for a missing one.
        return session.QueryAsync<GetCheckpointsResponse?>(
            "GetCheckpoints",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, jobId)),
            async (reader, token) =>
            {
                var found = false;
                var items = new List<GetCheckpointsItem>();
                while (await reader.ReadAsync(token))
                {
                    found = true;
                    if (!reader.IsDBNull(0))
                    {
                        items.Add(read(reader).ToItem());
                    }
                }

                return found
                    ? new GetCheckpointsResponse([
                        .. items.OrderBy(static item => item.Kind).ThenBy(static item => item.Name, StringComparer.Ordinal),
                    ])
                    : null;
            },
            ct
        );
    }
}
