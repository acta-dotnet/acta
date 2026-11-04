namespace Acta;

/// <summary>One page of <see cref="ICalendars.ListDependentsAsync"/>: the schedules naming a family, by path then binding.</summary>
public sealed record ListCalendarDependentsResponse(
    IReadOnlyList<ListCalendarDependentsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListCalendarDependentsItem>;
