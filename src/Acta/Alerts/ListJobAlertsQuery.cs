namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IAlerts.ListAsync"/>. Alerts are ordered newest
/// first (created_at_utc descending, id descending).
/// </summary>
/// <param name="JobNamespace">Restrict to one namespace.</param>
/// <param name="JobId">Restrict to one job's alerts.</param>
/// <param name="UnresolvedOnly">When true only unresolved alerts are returned.</param>
/// <param name="SeverityAtLeast">Minimum severity floor.</param>
/// <param name="DeliveryStatus">Restrict to one delivery status.</param>
/// <param name="Acknowledged">Null returns every alert; true restricts to acknowledged alerts; false restricts to unacknowledged ones.</param>
/// <param name="PageSize">Rows per page; null defaults to 50, values above 100 clamp to 100.</param>
/// <param name="Cursor">Opaque continuation cursor from the previous page's <see cref="PagedResult{T}.NextCursor"/>.</param>
/// <param name="IncludeTotal">Whether to also compute the filter-wide row count.</param>
/// <param name="Tags">Restrict to alerts carrying every supplied exact tag filter.</param>
public sealed record ListJobAlertsQuery(
    string? JobNamespace = null,
    long? JobId = null,
    bool? UnresolvedOnly = null,
    AlertSeverityCode? SeverityAtLeast = null,
    AlertDeliveryStatusCode? DeliveryStatus = null,
    bool? Acknowledged = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
