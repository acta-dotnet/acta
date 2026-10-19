using System.Globalization;

namespace Acta;

/// <summary><see cref="INamespaces.ListAsync"/>: namespaces by name ascending, one keyset page at a time.</summary>
internal sealed class ListNamespacesHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListNamespacesResponse> HandleAsync(ListNamespacesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var fragment = QueryValidation.ValidateNamespaceFragment(query.NameContains, nameof(query.NameContains));
        // Validation admits only name characters, so the LIKE pattern holds no wildcard but its own.
        var pattern = fragment is null ? null : "%" + fragment + "%";
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListNamespacesQuery));
        // The cursor's operation name predates the ListNamespaces stem; it stays so issued cursors still decode.
        var paging = Paging.Start(
            "ListNamespaceItems",
            "name asc",
            [CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("contains", fragment), ("status", ((byte?)query.Status)?.ToString(CultureInfo.InvariantCulture)), ("tags", tags)]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListNamespaces",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NameSearchFilter, pattern));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.StatusCode, query.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, (string?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListNamespacesItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.JobNamespace]);
        return new ListNamespacesResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
