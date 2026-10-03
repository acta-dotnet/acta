namespace Acta;

/// <summary>
/// One relay source as <see cref="IOutbox.ListSourcesAsync"/> reports it: the namespace's
/// <c>sys.outbox</c> slot plus the accounting parsed from the slot's persisted tick summary. The
/// summary (and the counters parsed from it) is null until the slot's first successful tick.
/// </summary>
/// <param name="JobNamespace">The worker namespace whose relay drains this source.</param>
/// <param name="SlotJobRef">The <c>sys.outbox</c> slot job's public ref (its audit trail holds the operator evidence).</param>
/// <param name="LastTickSummary">The last successful tick's rendered accounting, verbatim; null before the first success.</param>
/// <param name="Backlog">Pending rows still awaiting relay after the last tick; null when unknown.</param>
/// <param name="QuarantineTotal">Quarantined rows at the last tick; null when unknown.</param>
/// <param name="IsLocal">Whether this process registered the source, so <see cref="IOutbox.ListQuarantinedAsync"/> works here.</param>
public sealed record OutboxSourceListItem(
    string JobNamespace,
    JobRef SlotJobRef,
    string? LastTickSummary,
    long? Backlog,
    long? QuarantineTotal,
    bool IsLocal
);
