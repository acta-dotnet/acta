namespace Acta.Runtime;

/// <summary>How many roots the batch deleted.</summary>
internal readonly record struct PurgeExpiredDataResponse(int Purged);
