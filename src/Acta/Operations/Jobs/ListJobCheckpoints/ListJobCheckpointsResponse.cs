namespace Acta;

/// <summary>One page of <see cref="IJobs.ListCheckpointsAsync"/>: a job's checkpoint slots by kind, then name.</summary>
public sealed record ListJobCheckpointsResponse(
    IReadOnlyList<ListJobCheckpointsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListJobCheckpointsItem>;
