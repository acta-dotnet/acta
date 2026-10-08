namespace Acta.AspNetCore;

/// <summary>
/// Optional body of <c>POST /outbox/{jobNamespace}/requeue</c>; the route's namespace names the relay source. A null or
/// absent <c>OutboxIds</c> targets every quarantined row of that source.
/// </summary>
internal sealed record RequeueOutboxRequest(IReadOnlyList<Guid>? OutboxIds = null, string? ReasonMessage = null) : IControlRequest;
