namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IJobs.ListJobEventsAsync"/>. Events are ordered newest
/// first (created_at_utc descending, id descending). IncludeTotal requires a JobId scope because a
/// global event count is unbounded work.
/// </summary>
/// <param name="JobId">Restrict to one job's timeline.</param>
/// <param name="LineageRootId">Restrict to one lineage tree.</param>
/// <param name="JobNamespace">Restrict to one namespace.</param>
/// <param name="EventCode">Restrict to one event code.</param>
/// <param name="JobDefinitionId">Restrict to one definition's events (e.g. the definition.policy-changed audit trail).</param>
/// <param name="TenantId">Restrict to one tenant's job-scoped events (the resolved <c>tenants</c> id).</param>
/// <param name="WorkerId">Restrict to one worker's events (e.g. the worker.started/stopped/dead lifecycle trail).</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also compute the row count; allowed only with <paramref name="JobId"/>.</param>
/// <param name="Tags">Restrict to events carrying every supplied exact tag filter.</param>
public sealed record ListJobEventsQuery(
    long? JobId = null,
    long? LineageRootId = null,
    string? JobNamespace = null,
    JobEventCode? EventCode = null,
    int? JobDefinitionId = null,
    int? TenantId = null,
    int? WorkerId = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
