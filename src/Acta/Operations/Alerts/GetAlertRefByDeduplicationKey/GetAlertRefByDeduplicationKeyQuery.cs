namespace Acta;

/// <summary>The open alert to find by its deduplication key within a namespace, the key already canonical.</summary>
internal sealed record GetAlertRefByDeduplicationKeyQuery(int NamespaceId, string DeduplicationKey);
