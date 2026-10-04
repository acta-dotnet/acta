namespace Acta.Runtime;

/// <summary>
/// <see cref="IJobs.GetLineageMapAsync"/>: resolves the lookup, reads the focus, ancestors, steps, checkpoints,
/// and children in one round trip, and maps them.
/// </summary>
internal sealed class GetJobLineageMapHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<JobLineageMap?> HandleAsync(GetJobLineageMapQuery query, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(query.Job, ct) is not { } jobId)
        {
            return null;
        }

        // Clamp the child limit and fetch one extra so the mapper can flag a truncated set from the tail.
        var childLimit = Math.Clamp(query.Options?.ChildLimit ?? 100, 1, 1000);
        var data = await ReadAsync(jobId, childLimit + 1, ct);
        return data is null ? null : JobLineageMapper.Map(data, childLimit);
    }

    /// <summary>The lineage sets by job id with at most <paramref name="childFetchLimit"/> children, or null when no row matches.</summary>
    internal async ValueTask<JobLineageData?> ReadAsync(long jobId, int childFetchLimit, CancellationToken ct) =>
        await session.QueryAsync<JobLineageData?>(
            "GetJobLineageMap",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ChildFetchLimit, childFetchLimit));
            },
            static async (reader, token) =>
            {
                var readJob = DbProjectionResolver.Resolve<LineageJobRow>();
                var readStep = DbProjectionResolver.Resolve<LineageStepRow>();
                var readCheckpoint = DbProjectionResolver.Resolve<ExplainCheckpointRow>();
                var readChild = DbProjectionResolver.Resolve<LineageChildRow>();

                LineageJobRow? focus = null;
                while (await reader.ReadAsync(token))
                {
                    focus = readJob(reader);
                }

                var ancestors = new List<LineageJobRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        ancestors.Add(readJob(reader));
                    }
                }

                var steps = new List<LineageStepRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        steps.Add(readStep(reader));
                    }
                }

                var checkpoints = new List<ExplainCheckpointRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        checkpoints.Add(readCheckpoint(reader));
                    }
                }

                var children = new List<LineageChildRow>();
                if (await reader.NextResultAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        children.Add(readChild(reader));
                    }
                }

                return focus is null ? null : new JobLineageData(focus, ancestors, steps, checkpoints, children);
            },
            ct
        );
}
