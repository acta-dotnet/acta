namespace Acta;

/// <summary>
/// Filters and paging for <see cref="ILedger.ListEventsAsync"/>; newest first. Scopes: one job's timeline (<c>Job</c>), a lineage tree
/// (<c>LineageRoot</c>), one subject (<c>EntityKind</c> with an optional <c>EntityId</c>), a namespace, an event code, a definition (its
/// overrides audit trail), a tenant by id or name (its jobs' events and its own), a worker (<c>Worker</c>: the events it wrote and its
/// lifecycle), and a created window (<c>CreatedFromUtc</c> inclusive, <c>CreatedToUtc</c> exclusive). A job or worker filter that names
/// nothing answers an empty page. <c>PageSize</c> null defaults to 50 and clamps at 100; <c>Cursor</c> is the previous page's
/// <see cref="IPagedResponse{T}.NextCursor"/>; <c>IncludeTotal</c> is allowed only with one job or one subject.
/// </summary>
public sealed record ListEventsQuery(
    JobLookup? Job = null,
    JobLookup? LineageRoot = null,
    string? JobNamespace = null,
    EventCode? EventCode = null,
    EntityCode? EntityKind = null,
    long? EntityId = null,
    int? DefinitionId = null,
    int? TenantId = null,
    string? TenantName = null,
    WorkerRef? Worker = null,
    DateTime? CreatedFromUtc = null,
    DateTime? CreatedToUtc = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false
);
