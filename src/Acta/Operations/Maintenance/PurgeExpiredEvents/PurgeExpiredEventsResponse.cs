namespace Acta;

/// <summary>
/// How many events the batch deleted, and the last position it examined, where the next batch of the pass reads on; a
/// batch that found nothing answers the position it was given.
/// </summary>
internal readonly record struct PurgeExpiredEventsResponse(int Purged, DateTime LastCreatedUtc, long LastEventId);
