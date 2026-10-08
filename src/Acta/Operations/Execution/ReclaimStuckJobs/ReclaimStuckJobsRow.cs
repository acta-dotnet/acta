namespace Acta;

/// <summary>
/// One row returned by the reclaim routine for each touched job: a reclaimed lease, or with
/// <see cref="LaneRepaired"/> the released Blocked member of a stranded lane.
/// </summary>
[DbProjection]
internal readonly record struct ReclaimStuckJobsRow(long JobId, JobStatusCode ToStatus, long? ParentId, bool LaneRepaired);
