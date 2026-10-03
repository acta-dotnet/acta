namespace Acta.Runtime;

/// <summary>
/// <see cref="INamespaces.ListNamesAsync"/>: namespace names ascending, one keyset page at a time. The
/// names carry no status, so the status filter does not apply.
/// </summary>
internal sealed class ListNamespacesHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<PagedResult<string>> HandleAsync(ListNamespacesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (fragment, pattern) = NamespaceNames.Search(query);
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListNamespacesQuery));
        var paging = Paging.Start(
            "ListNamespaces",
            "name asc",
            [CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("contains", fragment), ("tags", tags)]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListNamespaces",
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
        return paging.Page(rows, total, static name => [name]);
    }
}
