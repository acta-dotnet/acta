namespace Acta.AspNetCore;

/// <summary>
/// One page of <see cref="IJobs.ListCheckpointsAsync"/> for the wire: the same page with each value rendered and capped as
/// the job's input is, since a <see cref="JobPayload"/> has no JSON form of its own.
/// </summary>
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
