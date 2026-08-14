using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Querying;

namespace Acta.Runtime.Modules.Execution.Tenants;

/// <summary>
/// Tenants feature behavior: the insert-or-return-existing registration by Acta-normalized tenant
/// key, the key-ordered list with cursor math, and the operator control verbs with actor stamping.
/// </summary>
internal sealed class TenantsService(ITenantStore store)
{
    private const string OrderTenants = "tenant_key asc";
    private const string ListOperationName = "ListTenants";

    // Operator/manual only: the actor is stamped here, never accepted from the caller.
    private static JobControlActor Operator(string? actorKey) =>
        new(ActorCode.Operator, JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey));

    private static string? Reason(string? msg) => msg.Truncate(ActaTextLimits.ReasonMessage);

    public async ValueTask<int> RegisterAsync(string tenantKey, string? displayName, string? description, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantKey);
        var key = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
        CatalogValidation.ValidateTenant(displayName, description);
        return await store.RegisterTenantAsync(new RegisterTenantCommand(key, displayName, description), ct);
    }

    public async ValueTask<TenantDetail?> GetAsync(string tenantKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantKey);
        var key = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
        var row = await store.GetTenantAsync(new TenantPointLookup(key, null), ct);
        return row is null
            ? null
            : new TenantDetail(
                row.TenantId,
                row.TenantKey,
                row.DisplayName,
                row.Description,
                row.Status,
                row.CreatedAtUtc,
                row.ModifiedAtUtc,
                row.Version
            );
    }

    public async ValueTask<PagedResult<TenantListItem>> ListAsync(ListTenantsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);

        // The providers perform a literal substring search against LOWER()ed columns. Keep the
        // normalized term free of LIKE metacharacters so %, _, and [ retain their literal meaning.
        var search = string.IsNullOrWhiteSpace(query.NameContains) ? null : query.NameContains.Trim().ToLowerInvariant();
        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListTenantsQuery));
        var filterHash = QueryFilterHash.Compute([
            ("search", search),
            ("status", ((byte?)query.Status)?.ToString(CultureInfo.InvariantCulture)),
            ("tags", tagFilters),
        ]);

        string? cursorTenantKey = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(query.Cursor, ListOperationName, OrderTenants, filterHash, [CursorKeyKind.Text]);
            cursorTenantKey = (string)keys[0];
        }

        var page = await store.ListTenantsAsync(
            new TenantPageRequest(search, query.Status, cursorTenantKey, pageSize + 1, query.IncludeTotal, tagFilters),
            ct
        );

        var rows = page.Rows;
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;
        var nextCursor = hasMore ? PageCursorCodec.Encode(ListOperationName, OrderTenants, filterHash, [items[^1].TenantKey]) : null;

        return new PagedResult<TenantListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    public async ValueTask<AdminControlResult> SuspendAsync(string tenantKey, string? reasonMessage, string? actorKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantKey);
        var key = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
        var outcome = await store.SuspendTenantAsync(new TenantControlCommand(key, Operator(actorKey), Reason(reasonMessage)), ct);
        return new AdminControlResult(outcome.Action, outcome.Version);
    }

    public async ValueTask<AdminControlResult> ResumeAsync(string tenantKey, string? reasonMessage, string? actorKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tenantKey);
        var key = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
        var outcome = await store.ResumeTenantAsync(new TenantControlCommand(key, Operator(actorKey), Reason(reasonMessage)), ct);
        return new AdminControlResult(outcome.Action, outcome.Version);
    }

    public async ValueTask<AdminControlResult> UpdateAsync(
        string tenantKey,
        string? displayName,
        string? description,
        int expectedVersion,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(tenantKey);
        var key = IdentifierSyntax.NormalizeTenantKey(tenantKey, nameof(tenantKey));
        CatalogValidation.ValidateTenant(displayName, description);
        var outcome = await store.UpdateTenantAsync(
            new UpdateTenantCommand(key, displayName, description, expectedVersion, Operator(actorKey), Reason(reasonMessage)),
            ct
        );
        return new AdminControlResult(outcome.Action, outcome.Version);
    }
}
