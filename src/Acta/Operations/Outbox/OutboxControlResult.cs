namespace Acta;

/// <summary>
/// Outcome of an <see cref="IOutbox"/> control verb (Requeue / Discard). These verbs park a durable
/// command that the next relay pass applies, so success is <see cref="ControlAction.Accepted"/>
/// (parked, with application owed to that pass); once applied, the evidence lands as an audit event
/// on the namespace's <c>sys.outbox</c> slot job.
/// </summary>
/// <remarks>
/// <see cref="PendingSinceUtc"/> is set only on <see cref="ControlAction.Rejected"/>: the park instant
/// of the same verb's still-unapplied command occupying the inbox. A rejection older than the
/// worker-dead window would have been superseded instead, so a persistent rejection with an old
/// instant means no relay is running to apply it.
/// </remarks>
/// <param name="Action">Accepted (parked), Rejected (an unapplied command is pending), or NotFound (no relay slot).</param>
/// <param name="PendingSinceUtc">When the blocking pending command was parked; null except on Rejected.</param>
public sealed record OutboxControlResult(ControlAction Action, DateTime? PendingSinceUtc);
