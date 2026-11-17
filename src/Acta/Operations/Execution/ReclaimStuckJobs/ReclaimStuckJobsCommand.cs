namespace Acta;

/// <summary>
/// The namespace whose lapsed leases to reclaim, and the workers a starting host released because their process had
/// ended: SQLite records their jobs as released, not as a lease that ran out.
/// </summary>
internal sealed record ReclaimStuckJobsCommand(int NamespaceId, IReadOnlyList<int>? ReleasedWorkerIds);
