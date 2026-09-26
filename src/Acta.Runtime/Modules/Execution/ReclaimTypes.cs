namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// One reclaim pass's outcome: how many leases were reclaimed, which children landed terminal Failed
/// (budget exhausted) with their parent ids, so the caller can raise each child-done latch, and how many
/// stranded lanes had their lowest Blocked member released.
/// </summary>
internal sealed record ReclaimStuckJobsResult(
    int Reclaimed,
    IReadOnlyList<(long ChildId, long ParentId)> FailedChildren,
    int RepairedLanes
);

/// <summary>
/// One row returned by the reclaim routine for each touched job: a reclaimed lease, or with
/// <see cref="LaneRepaired"/> the released Blocked member of a stranded lane.
/// </summary>
internal readonly record struct ReclaimedJobRow(long JobId, JobStatusCode ToStatus, long? ParentId, bool LaneRepaired);

/// <summary>Folds the reclaim rows into a <see cref="ReclaimStuckJobsResult"/>: counts plus the Failed children.</summary>
internal static class ReclaimResultMapper
{
    public static ReclaimStuckJobsResult Map(IReadOnlyList<ReclaimedJobRow> rows)
    {
        List<(long, long)>? failed = null;
        var repaired = 0;
        foreach (var row in rows)
        {
            if (row.LaneRepaired)
            {
                repaired++;
            }
            else if (row.ToStatus == JobStatusCode.Failed && row.ParentId is { } parentId)
            {
                (failed ??= []).Add((row.JobId, parentId));
            }
        }

        return new ReclaimStuckJobsResult(rows.Count - repaired, failed ?? (IReadOnlyList<(long, long)>)[], repaired);
    }
}
