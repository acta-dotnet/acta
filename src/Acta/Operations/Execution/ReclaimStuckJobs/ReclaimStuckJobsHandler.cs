namespace Acta;

/// <summary><see cref="IExecutionOperations.ReclaimStuckJobsAsync"/>: one routine call per namespace, its rows folded into the reclaim result.</summary>
internal sealed class ReclaimStuckJobsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ReclaimStuckJobsResponse> HandleAsync(ReclaimStuckJobsCommand command, CancellationToken ct) =>
        Map(
            await session.ExecuteAsync(
                new StoreCommand("ReclaimStuckJobs"),
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, command.NamespaceId)),
                DbProjectionResolver.Resolve<ReclaimStuckJobsRow>(),
                ct
            )
        );

    /// <summary>Folds the reclaim rows into counts plus the Failed children.</summary>
    private static ReclaimStuckJobsResponse Map(IReadOnlyList<ReclaimStuckJobsRow> rows)
    {
        List<(long, long)>? failed = null;
        var repaired = 0;
        foreach (var row in rows)
        {
            if (row.LaneRepaired)
            {
                repaired++;
            }
            else if (row.ToStatus == JobStatusCode.Failed && row.ParentId is { } parentId)
            {
                (failed ??= []).Add((row.JobId, parentId));
            }
        }

        return new ReclaimStuckJobsResponse(rows.Count - repaired, failed ?? (IReadOnlyList<(long, long)>)[], repaired);
    }
}
