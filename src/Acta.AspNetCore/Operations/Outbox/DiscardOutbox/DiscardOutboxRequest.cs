namespace Acta.AspNetCore;

/// <summary>
/// Optional body of the outbox discard: the quarantined rows to delete, and a reason. Without outboxIds it acts on every quarantined row
/// of the namespace.
/// </summary>
/// <param name="OutboxIds">The ids of the quarantined rows to act on, at least one; omitted or null acts on every quarantined row of the
/// namespace.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record DiscardOutboxRequest(IReadOnlyList<Guid>? OutboxIds = null, string? ReasonMessage = null) : IControlRequest;
