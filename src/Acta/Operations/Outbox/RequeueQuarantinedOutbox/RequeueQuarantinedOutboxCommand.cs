namespace Acta;

/// <summary>
/// Operator requeue of Quarantined rows: back to Pending, immediately due, failure budget reset,
/// <c>failure_message</c> kept as evidence. A null <paramref name="JobRefs"/> targets every quarantined row.
/// </summary>
internal sealed record RequeueQuarantinedOutboxCommand(IReadOnlyList<Guid>? JobRefs);
