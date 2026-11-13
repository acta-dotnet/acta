using System.Globalization;

namespace Acta;

/// <summary><see cref="IJobs.ListCheckpointsAsync"/>: resolves the lookup, then one keyset page of the job's checkpoints.</summary>
internal sealed class ListJobCheckpointsHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListJobCheckpointsResponse> HandleAsync(ListJobCheckpointsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // A job that is not there, or was purged between pages, has no checkpoints: answered before the cursor is read.
        if ((await getJobId.HandleAsync(new GetJobIdQuery(query.Job), ct))?.JobId is not { } id)
        {
            return new ListJobCheckpointsResponse(
                [],
                null,
                false,
                JobsQueryLimits.NormalizePageSize(query.PageSize),
                query.IncludeTotal ? 0 : null
            );
        }

        var paging = Paging.Start(
            "ListJobCheckpoints",
            "kind asc, name asc",
            [CursorKeyKind.Int, CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("job", id.ToString(CultureInfo.InvariantCulture))]
        );
        var (rows, total) = await session.QueryPageAsync(
            "ListJobCheckpoints",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, id));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCheckpointKind, (int?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCheckpointName, (string?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListJobCheckpointsRow>(),
            ct
        );
        var (page, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static row =>
            [(int)row.KindId, row.CheckpointName]
        );
        return new ListJobCheckpointsResponse([.. page.Select(static row => row.ToItem())], nextCursor, hasMore, pageSize, totalCount);
    }
}
