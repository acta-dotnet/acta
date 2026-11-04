using System.Globalization;

namespace Acta;

/// <summary><see cref="ITags.ListAsync"/>: resolves the target, then one keyset page of its tags by name.</summary>
internal sealed class ListTagsHandler(GetTagTargetHandler getTagTarget, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListTagsResponse> HandleAsync(ListTagsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Target, "target");

        // A definition or job that is not there has no tags: answered before the cursor is read. A tenant or namespace
        // is found by the SQL, which answers no rows for one that is not there.
        if (await getTagTarget.HandleAsync(new GetTagTargetQuery(query.Target), ct) is not { } target)
        {
            return new ListTagsResponse([], null, false, JobsQueryLimits.NormalizePageSize(query.PageSize), query.IncludeTotal ? 0 : null);
        }

        var paging = Paging.Start(
            "ListTags",
            "name asc",
            [CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [
                ("entity", ((byte)target.EntityCode).ToString(CultureInfo.InvariantCulture)),
                ("target", target.LookupId?.ToString(CultureInfo.InvariantCulture) ?? target.LookupName),
            ]
        );
        var (rows, total) = await session.QueryPageAsync(
            "ListTags",
            cmd =>
            {
                target.Bind(cmd, dialect);
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorTagName, (string?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListTagsItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.Name]);
        return new ListTagsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
