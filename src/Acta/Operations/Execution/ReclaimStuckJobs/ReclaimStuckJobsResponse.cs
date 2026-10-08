namespace Acta;

/// <summary>
/// One reclaim pass's outcome: how many leases were reclaimed, which children landed terminal Failed
/// (budget exhausted) with their parent ids, so the caller can raise each child-done latch, and how many
/// stranded lanes had their lowest Blocked member released.
/// </summary>
internal sealed record ReclaimStuckJobsResponse(
    int Reclaimed,
    IReadOnlyList<(long ChildId, long ParentId)> FailedChildren,
    int RepairedLanes
);
