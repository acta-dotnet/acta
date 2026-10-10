namespace Acta;

/// <summary>A discard of a source's quarantined rows to park; null <c>OutboxIds</c> targets every one.</summary>
internal sealed record DiscardOutboxCommand(string JobNamespace, IReadOnlyList<Guid>? OutboxIds, string? ReasonMessage, string? Actor);
