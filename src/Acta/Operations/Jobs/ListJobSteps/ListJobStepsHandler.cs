using System.Globalization;

namespace Acta;

/// <summary><see cref="IJobs.ListStepsAsync"/>: resolves the lookup, then one keyset page of the job's steps, newest first.</summary>
internal sealed class ListJobStepsHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListJobStepsResponse> HandleAsync(ListJobStepsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // A job that is not there, or was purged between pages, has no steps: answered before the cursor is read.
        if ((await getJobId.HandleAsync(new GetJobIdQuery(query.Job), ct))?.JobId is not { } id)
        {
            return new ListJobStepsResponse(
                [],
                null,
                false,
                JobsQueryLimits.NormalizePageSize(query.PageSize),
                query.IncludeTotal ? 0 : null
            );
        }

        var paging = Paging.Start(
            "ListJobSteps",
            "created_at_utc desc, step_name desc",
            [CursorKeyKind.Utc, CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("job", id.ToString(CultureInfo.InvariantCulture))]
        );
        var (rows, total) = await session.QueryPageAsync(
            "ListJobSteps",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.JobId, id));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorStepName, (string?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListJobStepsItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.CreatedAtUtc, item.Name]);
        return new ListJobStepsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
