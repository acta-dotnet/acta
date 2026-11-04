namespace Acta;

/// <summary>One page of <see cref="IJobs.ListStepsAsync"/>: a job's step slots, newest first.</summary>
public sealed record ListJobStepsResponse(
    IReadOnlyList<ListJobStepsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListJobStepsItem>;
