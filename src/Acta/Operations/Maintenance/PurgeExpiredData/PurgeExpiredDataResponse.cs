namespace Acta;

/// <summary>
/// How many roots the batch deleted, and for the jobs section where the walk continues: the next batch reads below
/// <see cref="NextJobId"/>, and zero means the walk reached the oldest row and starts again from the newest.
/// </summary>
internal readonly record struct PurgeExpiredDataResponse(int Purged, long NextJobId);
