namespace Acta;

/// <summary>A requeue of a source's quarantined rows to park; null <c>OutboxIds</c> targets every one.</summary>
internal sealed record RequeueOutboxCommand(string JobNamespace, IReadOnlyList<Guid>? OutboxIds, string? ReasonMessage, string? ActorKey);
