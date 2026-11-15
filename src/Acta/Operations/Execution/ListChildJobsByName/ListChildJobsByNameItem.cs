namespace Acta;

/// <summary>One child found by its name, the deduplication key its parent started it under.</summary>
[DbProjection(ByName = true)]
internal sealed record ListChildJobsByNameItem(long JobId, Guid JobRef, string DeduplicationKey);
