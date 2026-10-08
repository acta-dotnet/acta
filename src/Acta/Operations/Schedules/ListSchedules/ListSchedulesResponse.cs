namespace Acta;

/// <summary>One page of <see cref="ISchedules.ListAsync"/>.</summary>
public sealed record ListSchedulesResponse(
    IReadOnlyList<ListSchedulesItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListSchedulesItem>;
