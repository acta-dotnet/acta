namespace Acta;

/// <summary>
/// How many jobs the batch deleted, and where the walk continues: the next batch reads the jobs before <see cref="Next"/>,
/// and null means the walk reached the namespace's oldest job, which ends its cycle.
/// </summary>
internal readonly record struct PurgeExpiredJobsResponse(int Purged, RetentionCursor? Next);
