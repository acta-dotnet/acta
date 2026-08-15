namespace Acta.AspNetCore.Features.Outbox;

/// <summary>
/// Body of an outbox requeue/discard POST. The source is addressed by its worker namespace; a null
/// <c>OutboxIds</c> targets every quarantined row of that source. The framework stamps the actor
/// itself, so the body carries only the scope and the operator note.
/// </summary>
internal sealed record OutboxControlRequest(
    string? JobNamespace = null,
    IReadOnlyList<Guid>? OutboxIds = null,
    string? ReasonMessage = null
);
