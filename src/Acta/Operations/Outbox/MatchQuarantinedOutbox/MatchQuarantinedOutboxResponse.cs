namespace Acta;

/// <summary>The ids of the Quarantined rows the command targets now.</summary>
internal readonly record struct MatchQuarantinedOutboxResponse(IReadOnlyList<Guid> OutboxIds);
