namespace Acta;

/// <summary>One page of <see cref="ILedger.ListJobsAsync"/>.</summary>
public sealed record ListJobsResponse(IReadOnlyList<ListJobsItem> Items, string? NextCursor, bool HasMore, int PageSize, long? TotalCount)
    : IPagedResponse<ListJobsItem>;
