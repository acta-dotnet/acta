namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IJobs.ListJobsAsync"/>. Jobs are ordered newest first
/// (created_at_utc descending, id descending).
/// </summary>
/// <param name="JobNamespace">Restrict to one namespace; required when <paramref name="JobName"/> is set.</param>
/// <param name="Status">Restrict to one job status.</param>
/// <param name="JobName">Restrict to one job definition name within <paramref name="JobNamespace"/>.</param>
/// <param name="ParentJobId">Restrict to the direct children of one job.</param>
/// <param name="TenantId">Restrict to one tenant (the resolved <c>tenants</c> id).</param>
/// <param name="CorrelationKey">Restrict to jobs stamped with this exact correlation id (a trace / request / order id). Case-sensitive: the value is matched verbatim against the stored id, which Acta never canonicalizes.</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also compute the filter-wide row count.</param>
/// <param name="Tags">Restrict to jobs carrying every supplied tag filter.</param>
public sealed record ListJobsQuery(
    string? JobNamespace = null,
    JobStatusCode? Status = null,
    string? JobName = null,
    long? ParentJobId = null,
    int? TenantId = null,
    string? CorrelationKey = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
