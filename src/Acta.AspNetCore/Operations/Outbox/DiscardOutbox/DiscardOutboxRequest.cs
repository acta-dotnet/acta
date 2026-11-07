namespace Acta.AspNetCore;

/// <summary>
/// Optional body of <c>POST /outbox/{jobNamespace}/discard</c>; the route's namespace names the relay source. A null or
/// absent <c>OutboxIds</c> targets every quarantined row of that source.
/// </summary>
/// <param name="OutboxIds">The ids of the quarantined rows to act on, at least one; omitted or null acts on every quarantined row of the
/// namespace.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record DiscardOutboxRequest(IReadOnlyList<Guid>? OutboxIds = null, string? ReasonMessage = null) : IControlRequest;
