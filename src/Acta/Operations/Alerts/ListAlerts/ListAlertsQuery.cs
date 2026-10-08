namespace Acta;

/// <summary>
/// Filters and paging for <see cref="IAlerts.ListAsync"/>; alerts are ordered newest first (created_at_utc descending,
/// id descending). <c>AcknowledgedOnly</c> null returns every alert, true the acknowledged and false the unacknowledged;
/// <c>PageSize</c> null defaults to 50 and values above 100 clamp; <c>Cursor</c> is the previous page's
/// <see cref="IPagedResponse{T}.NextCursor"/>; <c>Tags</c> restricts to alerts carrying every supplied exact tag filter.
/// </summary>
public sealed record ListAlertsQuery(
    string? JobNamespace = null,
    long? JobId = null,
    bool? UnresolvedOnly = null,
    AlertSeverityCode? SeverityAtLeast = null,
    AlertDeliveryStatusCode? DeliveryStatus = null,
    bool? AcknowledgedOnly = null,
    int? PageSize = null,
    string? Cursor = null,
    bool IncludeTotal = false,
    IReadOnlyList<TagFilter>? Tags = null
);
