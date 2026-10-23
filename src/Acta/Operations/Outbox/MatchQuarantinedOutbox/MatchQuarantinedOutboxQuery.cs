namespace Acta;

/// <summary>The id filter of an operator command; null targets every quarantined row.</summary>
internal sealed record MatchQuarantinedOutboxQuery(IReadOnlyList<Guid>? OutboxIds);
