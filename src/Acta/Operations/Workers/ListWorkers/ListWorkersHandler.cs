using System.Globalization;

namespace Acta;

/// <summary><see cref="IWorkers.ListAsync"/>: workers most recently seen first, one keyset page at a time.</summary>
internal sealed class ListWorkersHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListWorkersResponse> HandleAsync(ListWorkersQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));

        var paging = Paging.Start(
            "ListWorkers",
            "last_seen_at_utc desc, id desc",
            [CursorKeyKind.Utc, CursorKeyKind.Int],
            query.Cursor,
            query.PageSize,
            [("ns", jobNamespace), ("status", ((byte?)query.Status)?.ToString(CultureInfo.InvariantCulture))]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListWorkers",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.StatusCode, query.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorLastSeenAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorIntId, (int?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListWorkersItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(
            rows,
            total,
            static item => [item.LastSeenAtUtc, item.WorkerId]
        );
        return new ListWorkersResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
