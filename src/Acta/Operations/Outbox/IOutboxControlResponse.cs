namespace Acta;

/// <summary>
/// What an outbox control verb did. The verbs park a durable command that the next relay pass applies, so
/// success is Accepted; once applied, the evidence lands as an audit event on the namespace's
/// <c>sys.outbox</c> slot job. Rejected means an unapplied command of the same verb is pending; NotFound
/// means the namespace has no relay slot.
/// </summary>
public interface IOutboxControlResponse
{
    /// <summary>Accepted (parked), Rejected (an unapplied command is pending), or NotFound (no relay slot).</summary>
    ControlAction Action { get; }

    /// <summary>
    /// When the pending command that caused a rejection was parked; null except on Rejected. An instant older
    /// than the worker-dead window would have been superseded instead, so an old one means no relay is running.
    /// </summary>
    DateTime? PendingSinceUtc { get; }
}
