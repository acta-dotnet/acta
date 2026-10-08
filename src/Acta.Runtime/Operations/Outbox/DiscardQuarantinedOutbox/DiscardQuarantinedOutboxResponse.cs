namespace Acta.Runtime;

/// <summary>The ids of the rows the discard deleted.</summary>
internal readonly record struct DiscardQuarantinedOutboxResponse(IReadOnlyList<Guid> OutboxIds);
