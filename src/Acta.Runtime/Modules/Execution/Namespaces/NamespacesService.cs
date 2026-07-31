using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Querying;

namespace Acta.Runtime.Modules.Execution.Namespaces;

/// <summary>
/// Namespaces feature behavior: the name-list validation and cursor math plus operator
/// suspend/resume/metadata rules. The seeded sys namespace (id 1 / name sys) is rejected here before
/// any store call runs.
/// </summary>
internal sealed class NamespacesService(INamespaceStore store)
{
    private const string OrderNamespaces = "name asc";
    private const string ListOperationName = "ListNamespaces";
    private const string ListItemsOperationName = "ListNamespaceItems";

    private static JobControlActor Operator(string? actorKey) =>
        new(JobActorCode.Operator, JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey));

    private static string? Reason(string? msg) => msg.Truncate(ActaTextLimits.ReasonMessage);

    // Lookup-permissive shape validation, then an explicit sys rejection: a well-formed non-sys name
    // passes, sys throws ArgumentException, a malformed name throws the kebab ArgumentException.
    private static string ResolveWritableName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var canonical = IdentifierSyntax.CanonicalizeKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        return IdentifierSyntax.IsReservedSystemName(canonical)
            ? throw new ArgumentException("The system namespace sys cannot be suspended or edited.", nameof(name))
            : canonical;
    }

    public async ValueTask<PagedResult<string>> ListAsync(ListNamespacesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with { NameContains = QueryValidation.ValidateNamespaceFragment(query.NameContains, nameof(query.NameContains)) };

        var nameSearch = query.NameContains is null ? null : "%" + query.NameContains + "%";
        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListNamespacesQuery));
        var filterHash = QueryFilterHash.Compute([("contains", query.NameContains), ("tags", tagFilters)]);

        string? cursorName = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(query.Cursor, ListOperationName, OrderNamespaces, filterHash, [CursorKeyKind.Text]);
            cursorName = (string)keys[0];
        }

        // The name-only list projects no status, so it does not honor the status filter.
        var page = await store.ListNamespacesAsync(
            new NamespacePageRequest(nameSearch, null, cursorName, pageSize + 1, query.IncludeTotal, tagFilters),
            ct
        );

        var rows = page.Rows;
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;
        var nextCursor = hasMore ? PageCursorCodec.Encode(ListOperationName, OrderNamespaces, filterHash, [items[^1]]) : null;

        return new PagedResult<string>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    public async ValueTask<PagedResult<NamespaceListItem>> ListItemsAsync(ListNamespacesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with { NameContains = QueryValidation.ValidateNamespaceFragment(query.NameContains, nameof(query.NameContains)) };

        var nameSearch = query.NameContains is null ? null : "%" + query.NameContains + "%";
        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListNamespacesQuery));
        var filterHash = QueryFilterHash.Compute([
            ("contains", query.NameContains),
            ("status", ((byte?)query.Status)?.ToString(CultureInfo.InvariantCulture)),
            ("tags", tagFilters),
        ]);

        string? cursorName = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(query.Cursor, ListItemsOperationName, OrderNamespaces, filterHash, [CursorKeyKind.Text]);
            cursorName = (string)keys[0];
        }

        var page = await store.ListNamespaceItemsAsync(
            new NamespacePageRequest(nameSearch, query.Status, cursorName, pageSize + 1, query.IncludeTotal, tagFilters),
            ct
        );

        var rows = page.Rows;
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;
        var nextCursor = hasMore ? PageCursorCodec.Encode(ListItemsOperationName, OrderNamespaces, filterHash, [items[^1].Name]) : null;

        return new PagedResult<NamespaceListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    public async ValueTask<AdminControlResult> SuspendAsync(string name, string? reasonMessage, string? actorKey, CancellationToken ct)
    {
        var canonical = ResolveWritableName(name);
        var outcome = await store.SuspendNamespaceAsync(
            new NamespaceControlCommand(canonical, Operator(actorKey), Reason(reasonMessage)),
            ct
        );
        return new AdminControlResult(outcome.Action, outcome.Version);
    }

    public async ValueTask<AdminControlResult> ResumeAsync(string name, string? reasonMessage, string? actorKey, CancellationToken ct)
    {
        var canonical = ResolveWritableName(name);
        var outcome = await store.ResumeNamespaceAsync(
            new NamespaceControlCommand(canonical, Operator(actorKey), Reason(reasonMessage)),
            ct
        );
        return new AdminControlResult(outcome.Action, outcome.Version);
    }

    public async ValueTask<AdminControlResult> UpdateMetadataAsync(
        string name,
        string? ownerTeam,
        string? description,
        int expectedVersion,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    )
    {
        var canonical = ResolveWritableName(name);
        CatalogMetadataValidation.ValidateNamespace(ownerTeam, description);
        var outcome = await store.UpdateNamespaceMetadataAsync(
            new UpdateNamespaceMetadataCommand(
                canonical,
                ownerTeam,
                description,
                expectedVersion,
                Operator(actorKey),
                Reason(reasonMessage)
            ),
            ct
        );
        return new AdminControlResult(outcome.Action, outcome.Version);
    }
}
