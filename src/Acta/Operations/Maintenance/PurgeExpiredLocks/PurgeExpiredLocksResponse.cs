namespace Acta;

/// <summary>How many lock rows the batch deleted.</summary>
internal readonly record struct PurgeExpiredLocksResponse(int Purged);
