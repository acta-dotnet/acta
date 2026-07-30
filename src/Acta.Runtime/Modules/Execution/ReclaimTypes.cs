namespace Acta.Modules.Execution;

/// <summary>
/// One reclaim pass's outcome: how many leases were reclaimed and which children landed terminal
/// Failed (budget exhausted) with their parent ids, so the caller can raise each child-done latch.
/// </summary>
internal sealed record ReclaimStuckJobsResult(int Reclaimed, IReadOnlyList<(long ChildId, long ParentId)> FailedChildren);

/// <summary>
/// One row returned by the reclaim routine for each touched job.
/// </summary>
internal readonly record struct ReclaimedJobRow(long JobId, JobStatusCode ToStatus, long? ParentId);

/// <summary>Folds the reclaim rows into a <see cref="ReclaimStuckJobsResult"/>: count plus the Failed children.</summary>
internal static class ReclaimResultMapper
{
    public static ReclaimStuckJobsResult Map(IReadOnlyList<ReclaimedJobRow> rows)
    {
        List<(long, long)>? failed = null;
        foreach (var row in rows)
        {
            if (row.ToStatus == JobStatusCode.Failed && row.ParentId is { } parentId)
            {
                (failed ??= []).Add((row.JobId, parentId));
            }
        }

        return new ReclaimStuckJobsResult(rows.Count, failed ?? (IReadOnlyList<(long, long)>)[]);
    }
}
