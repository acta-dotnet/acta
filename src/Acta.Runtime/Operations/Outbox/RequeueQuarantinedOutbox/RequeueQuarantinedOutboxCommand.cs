namespace Acta.Runtime;

/// <summary>
/// Operator requeue of Quarantined rows: back to Pending, immediately due, failure budget reset,
/// <c>last_error</c> kept as evidence. A null <paramref name="OutboxIds"/> targets every quarantined row.
/// </summary>
internal sealed record RequeueQuarantinedOutboxCommand(IReadOnlyList<Guid>? OutboxIds);
