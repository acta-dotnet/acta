namespace Acta;

/// <summary>One page of <see cref="IAlerts.ListAsync"/>.</summary>
public sealed record ListAlertsResponse(
    IReadOnlyList<ListAlertsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListAlertsItem>;
