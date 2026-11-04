namespace Acta;

/// <summary>The deduplication key to resolve within a namespace, both already canonical.</summary>
internal sealed record GetJobIdByDeduplicationKeyQuery(string JobNamespace, string DeduplicationKey);
