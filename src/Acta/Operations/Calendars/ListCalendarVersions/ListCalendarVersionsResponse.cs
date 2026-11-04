namespace Acta;

/// <summary>One page of <see cref="ICalendars.ListVersionsAsync"/>: a family's versions, newest first.</summary>
public sealed record ListCalendarVersionsResponse(
    IReadOnlyList<ListCalendarVersionsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListCalendarVersionsItem>;
