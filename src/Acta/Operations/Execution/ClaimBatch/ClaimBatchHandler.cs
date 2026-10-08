namespace Acta;

/// <summary><see cref="Execution.ClaimBatchAsync"/>: one routine call claims up to the batch; a batch of none claims nothing.</summary>
internal sealed class ClaimBatchHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ClaimBatchResponse> HandleAsync(ClaimBatchCommand command, CancellationToken ct)
    {
        var options = command.Options;
        if (options.MaxBatch <= 0)
        {
            return ClaimBatchResponse.Empty;
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("ClaimBatch"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, options.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, options.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ClaimLimit, options.MaxBatch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StartExecuting, options.StartExecuting));
                dialect.BindExcludedDefinitionIds(cmd, options.ExcludedDefinitionIds);
            },
            DbProjectionResolver.Resolve<ClaimBatchRow>(),
            ct
        );
        return Map(rows);
    }

    /// <summary>Job rows become claimed jobs; the sentinel row, its job id null, becomes the horizon.</summary>
    private static ClaimBatchResponse Map(IReadOnlyList<ClaimBatchRow> rows)
    {
        if (rows.Count == 0)
        {
            return ClaimBatchResponse.Empty;
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

        return new ClaimBatchResponse(jobs, horizon);
    }
}
