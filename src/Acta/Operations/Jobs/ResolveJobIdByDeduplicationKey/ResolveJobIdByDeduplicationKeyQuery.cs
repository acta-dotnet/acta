namespace Acta;

/// <summary>The deduplication key to resolve within a namespace, both already canonical.</summary>
internal sealed record ResolveJobIdByDeduplicationKeyQuery(string JobNamespace, string DeduplicationKey);
