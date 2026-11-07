namespace Acta;

/// <summary>One page of a job's checkpoints, by kind, then name.</summary>
public sealed record ListJobCheckpointsResponse(
    IReadOnlyList<ListJobCheckpointsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListJobCheckpointsItem>;
