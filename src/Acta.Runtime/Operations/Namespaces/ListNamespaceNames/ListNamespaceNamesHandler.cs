namespace Acta.Runtime;

/// <summary>
/// <see cref="INamespaces.ListNamesAsync"/>: namespace names ascending, one keyset page at a time.
/// </summary>
internal sealed class ListNamespaceNamesHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListNamespaceNamesResponse> HandleAsync(ListNamespaceNamesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (fragment, pattern) = NamespaceNames.Search(query.NameContains);
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListNamespaceNamesQuery));
        // The cursor's operation name predates the ListNamespaceNames stem; it stays so issued cursors still decode.
        var paging = Paging.Start(
            "ListNamespaces",
            "name asc",
            [CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("contains", fragment), ("tags", tags)]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListNamespaceNames",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NameSearchFilter, pattern));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, (string?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            static reader => reader.GetString(0),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static name => [name]);
        return new ListNamespaceNamesResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
