namespace Acta;

/// <summary>
/// How many jobs the batch deleted, and where the walk continues: the next batch reads the expired jobs after
/// <see cref="Next"/>, and null means the batch reached the end of the expired range, so the walk starts again at the
/// oldest.
/// </summary>
internal readonly record struct PurgeExpiredJobsResponse(int Purged, RetentionCursor? Next);
