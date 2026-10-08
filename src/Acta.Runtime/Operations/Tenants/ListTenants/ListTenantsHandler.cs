using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="ITenants.ListAsync"/>: tenants by key ascending, one keyset page at a time.</summary>
internal sealed class ListTenantsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListTenantsResponse> HandleAsync(ListTenantsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The SQL matches the lower-cased term as a literal substring of the lower-cased columns.
        var search = string.IsNullOrWhiteSpace(query.NameContains) ? null : query.NameContains.Trim().ToLowerInvariant();
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListTenantsQuery));
        var paging = Paging.Start(
            "ListTenants",
            "tenant_key asc",
            [CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("search", search), ("status", ((byte?)query.Status)?.ToString(CultureInfo.InvariantCulture)), ("tags", tags)]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListTenants",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TenantSearch, search));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Tenant.StatusCode, query.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorTenantKey, (string?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListTenantsItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.TenantKey]);
        return new ListTenantsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
