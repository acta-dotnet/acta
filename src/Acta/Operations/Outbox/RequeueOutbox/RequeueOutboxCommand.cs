namespace Acta;

/// <summary>A requeue of a source's quarantined rows to park; null <c>JobRefs</c> targets every one.</summary>
internal sealed record RequeueOutboxCommand(string JobNamespace, IReadOnlyList<Guid>? JobRefs, string? ReasonMessage, string? Actor);
