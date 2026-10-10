namespace Acta;

/// <summary><see cref="IExecutionOperations.ClaimJobsAsync"/>: one routine call claims up to the batch; a batch of none claims nothing.</summary>
internal sealed class ClaimJobsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ClaimJobsResponse> HandleAsync(ClaimJobsCommand command, CancellationToken ct)
    {
        var options = command.Options;
        if (options.MaxBatch <= 0)
        {
            return ClaimJobsResponse.Empty;
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("ClaimJobs"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, options.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, options.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ClaimLimit, options.MaxBatch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StartExecuting, options.StartExecuting));
                dialect.BindExcludedDefinitionIds(cmd, options.ExcludedDefinitionIds);
            },
            DbProjectionResolver.Resolve<ClaimJobsRow>(),
            ct
        );
        return Map(rows);
    }

    /// <summary>Job rows become claimed jobs; the sentinel row, its job id null, becomes the horizon.</summary>
    private static ClaimJobsResponse Map(IReadOnlyList<ClaimJobsRow> rows)
    {
        if (rows.Count == 0)
        {
            return ClaimJobsResponse.Empty;
        }

        ClaimHorizon? horizon = null;
        var jobs = new List<ClaimedJob>(rows.Count);
        foreach (var row in rows)
        {
            if (row.JobId is null)
            {
                horizon = new ClaimHorizon(
                    row.DbNowUtc ?? throw new InvalidOperationException("claim returned a horizon row with NULL db_now."),
                    row.NextReadyAtUtc
                );
            }
            else
            {
                jobs.Add(ClaimedJob.From(row));
            }
        }

        return new ClaimJobsResponse(jobs, horizon);
    }
}
