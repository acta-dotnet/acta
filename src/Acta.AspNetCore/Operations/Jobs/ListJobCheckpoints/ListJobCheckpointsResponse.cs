namespace Acta.AspNetCore;

/// <summary>
/// One page of a job's checkpoints, by kind, then name, each value a stored payload rendered and capped as the job's
/// input is.
/// </summary>
/// <remarks>
/// <see cref="IJobs.ListCheckpointsAsync"/>'s page for the wire, since a <see cref="JobPayload"/> has no JSON form of its
/// own.
/// </remarks>
/// <param name="Items">The page's items, at most pageSize of them.</param>
/// <param name="NextCursor">The cursor to pass back for the next page; null on the last page.</param>
/// <param name="HasMore">Whether more items exist beyond this page.</param>
/// <param name="PageSize">The page size the server used, after defaulting and clamping.</param>
/// <param name="TotalCount">The number of rows the filter matches; null unless the request set includeTotal.</param>
internal sealed record ListJobCheckpointsResponse(
    IReadOnlyList<ListJobCheckpointsItem> Items,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    long? TotalCount
) : IPagedResponse<ListJobCheckpointsItem>
{
    public static ListJobCheckpointsResponse From(Acta.ListJobCheckpointsResponse page, int maxInlineBytes) =>
        new(
            [.. page.Items.Select(item => ListJobCheckpointsItem.From(item, maxInlineBytes))],
            page.NextCursor,
            page.HasMore,
            page.PageSize,
            page.TotalCount
        );
}
