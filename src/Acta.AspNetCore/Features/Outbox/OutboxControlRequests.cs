namespace Acta.AspNetCore.Features.Outbox;

/// <summary>
/// Optional body of an outbox requeue/discard POST (the source is addressed by the route's
/// namespace). A null or absent <c>OutboxIds</c> targets every quarantined row of that source. The
/// framework stamps the actor itself, so the body carries only the scope and the operator note.
/// </summary>
internal sealed record OutboxControlRequest(IReadOnlyList<Guid>? OutboxIds = null, string? ReasonMessage = null);
