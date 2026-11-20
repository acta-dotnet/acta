namespace Acta;

/// <summary>How many worker rows the batch deleted.</summary>
internal readonly record struct PurgeExpiredWorkersResponse(int Purged);
