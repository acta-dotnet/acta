namespace Acta;

/// <summary>
/// How many roots the batch deleted, and for the jobs section where the walk continues: the next batch reads the jobs
/// before <see cref="Next"/>, and null means the walk reached the namespace's oldest job, which ends its cycle.
/// </summary>
internal readonly record struct PurgeExpiredDataResponse(int Purged, RetentionCursor? Next);
