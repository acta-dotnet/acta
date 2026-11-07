namespace Acta.AspNetCore;

/// <summary>
/// Body of the outbox discard: the quarantined rows to delete, or all of them, and a reason. It takes exactly one of outboxIds
/// and all, and answers 400 for neither or both.
/// </summary>
/// <param name="OutboxIds">The ids of the quarantined rows to delete, at least one; leave it out when all is true.</param>
/// <param name="All">True deletes every quarantined row of the namespace; leave it out, or false, when outboxIds names the rows.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record DiscardOutboxRequest(IReadOnlyList<Guid>? OutboxIds = null, bool All = false, string? ReasonMessage = null)
    : IControlRequest;
