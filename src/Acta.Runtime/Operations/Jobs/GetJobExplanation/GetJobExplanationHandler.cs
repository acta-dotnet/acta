namespace Acta.Runtime;

/// <summary>
/// <see cref="IJobs.ExplainAsync"/>: resolves the lookup, reads the header, steps, and checkpoints in one round
/// trip, and narrates them against the database clock, so "lease expired 2m ago" measures against the clock
/// that stamped the rows rather than this process's.
/// </summary>
internal sealed class GetJobExplanationHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect, IActaClock clock)
{
    public async ValueTask<JobExplanation?> HandleAsync(GetJobExplanationQuery query, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(query.Job, ct) is not { } jobId || await ReadAsync(jobId, ct) is not { } data)
        {
            return null;
        }

        return JobExplainer.Explain(data, await clock.GetUtcNowAsync(ct));
    }

    /// <summary>The explanation header plus its step and checkpoint sets by job id, or null when no row matches.</summary>
    internal async ValueTask<JobExplanationRows?> ReadAsync(long jobId, CancellationToken ct) =>
        await session.QueryAsync<JobExplanationRows?>(
            "GetJobExplanation",
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId)),
            static async (reader, token) =>
            {
                var readHeader = DbProjectionResolver.Resolve<ExplainHeaderRow>();
                var readStep = DbProjectionResolver.Resolve<ExplainStepRow>();
                var readCheckpoint = DbProjectionResolver.Resolve<ExplainCheckpointRow>();

                ExplainHeaderRow? header = null;
                while (await reader.ReadAsync(token))
                {
                    header = readHeader(reader);
                }

                var steps = new List<ExplainStepRow>();
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

                return header is null ? null : new JobExplanationRows(header, steps, checkpoints);
            },
            ct
        );
}
