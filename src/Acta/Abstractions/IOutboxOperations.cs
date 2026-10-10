namespace Acta;

/// <summary>
/// <see cref="IOutbox"/> plus the relay's calls into the group: the slot's operator-command inbox, Signal-kind
/// checkpoint rows on the slot job, durable, cross-peer, and bounded by the two fixed names, where operators park
/// commands that the relay tick reads, applies, records, and consumes; and the producer's source the tick drains.
/// </summary>
internal interface IOutboxOperations : IOutbox
{
    /// <summary>Reads the slot's pending command under <paramref name="name"/>, or null when that inbox slot is empty.</summary>
    Task<GetOutboxSignalResponse?> GetOutboxSignalAsync(long slotJobId, string name, CancellationToken ct);

    /// <summary>
    /// Version-CAS delete of an applied command row. A command superseded while it was being applied is not
    /// consumed; the newer command simply applies on the next tick.
    /// </summary>
    Task<ConsumeOutboxSignalResponse> ConsumeOutboxSignalAsync(long slotJobId, string name, int version, CancellationToken ct);

    /// <summary>Appends the applied-command evidence event against the slot job; it is always audited.</summary>
    Task RecordOutboxEventAsync(RecordOutboxEventCommand command, CancellationToken ct);

    /// <summary>Opens a producer's outbox table over the connection its provider package built; opening contacts no database.</summary>
    IOutboxSource OpenSource(OutboxSourceConnection connection);
}
