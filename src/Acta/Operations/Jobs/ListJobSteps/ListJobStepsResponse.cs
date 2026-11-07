namespace Acta;

/// <summary>One page of <see cref="IJobs.ListStepsAsync"/>: a job's steps, newest first.</summary>
/// <param name="Items">The page's items, at most pageSize of them.</param>
/// <param name="NextCursor">The cursor to pass back for the next page; null on the last page.</param>
/// <param name="HasMore">Whether more items exist beyond this page.</param>
/// <param name="PageSize">The page size the server used, after defaulting and clamping.</param>
/// <param name="TotalCount">The number of rows the filter matches; null unless the request set includeTotal.</param>
public sealed record ListJobStepsResponse(
    IReadOnlyList<ListJobStepsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListJobStepsItem>;
