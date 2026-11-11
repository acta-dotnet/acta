namespace Acta;

/// <summary>
/// Operator requeue of Quarantined rows: back to Pending, immediately due, failure budget reset,
/// <c>failure_message</c> kept as evidence. A null <paramref name="OutboxIds"/> targets every quarantined row.
/// </summary>
internal sealed record RequeueQuarantinedOutboxCommand(IReadOnlyList<Guid>? OutboxIds);
