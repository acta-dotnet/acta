namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ILedger.ListEventsAsync"/>; newest first. Scopes: one job's timeline, a lineage
/// tree, a namespace, an event code, a definition (its overrides audit trail), a tenant by id or key, a worker (its
/// lifecycle trail), an actor code, a reason code, a created window (<c>CreatedFromUtc</c> inclusive,
/// <c>CreatedToUtc</c> exclusive), and <c>Tags</c> carrying every supplied exact filter. <c>PageSize</c> null defaults
/// to 50 and clamps at 100; <c>Cursor</c> is the previous page's <see cref="PagedResult{T}.NextCursor"/>;
/// <c>IncludeTotal</c> is allowed only with <paramref name="JobId"/>.
/// </summary>
public sealed record ListEventsQuery(
    long? JobId = null,
    long? LineageRootId = null,
    string? JobNamespace = null,
    EventCode? EventCode = null,
    int? DefinitionId = null,
    int? TenantId = null,
    string? TenantKey = null,
    int? WorkerId = null,
    ActorCode? ActorCode = null,
    EventReasonCode? ReasonCode = null,
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
