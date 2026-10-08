namespace Acta.AspNetCore;

/// <summary>
/// Optional body of <c>POST /outbox/{jobNamespace}/discard</c>; the route's namespace names the relay source. A null or
/// absent <c>OutboxIds</c> targets every quarantined row of that source.
/// </summary>
internal sealed record DiscardOutboxRequest(IReadOnlyList<Guid>? OutboxIds = null, string? ReasonMessage = null) : IControlRequest;
