using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="INamespaces.ListAsync"/>: namespaces by name ascending, one keyset page at a time.</summary>
internal sealed class ListNamespaceItemsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<PagedResult<NamespaceListItem>> HandleAsync(ListNamespaceItemsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (fragment, pattern) = NamespaceNames.Search(query.NameContains);
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListNamespaceItemsQuery));
        var paging = Paging.Start(
            "ListNamespaceItems",
            "name asc",
            [CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("contains", fragment), ("status", ((byte?)query.Status)?.ToString(CultureInfo.InvariantCulture)), ("tags", tags)]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListNamespaceItems",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NameSearchFilter, pattern));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.StatusCode, query.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, (string?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<NamespaceListItem>(),
            ct
        );
        return paging.Page(rows, total, static item => [item.JobNamespace]);
    }
}
