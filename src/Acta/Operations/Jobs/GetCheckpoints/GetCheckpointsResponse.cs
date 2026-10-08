namespace Acta;

/// <summary>
/// The checkpoint slots of one existing job, returned by <see cref="IJobs.GetCheckpointsAsync"/>: an immutable snapshot
/// ordered by kind, then by name using ordinal comparison, and empty when the job has no slots.
/// </summary>
public sealed record GetCheckpointsResponse(IReadOnlyList<GetCheckpointsItem> Items);
