namespace Acta;

/// <summary>
/// One relay source as <see cref="IOutbox.ListSourcesAsync"/> reports it: the namespace's <c>sys.outbox</c> slot
/// (<c>SlotJobRef</c>, whose audit trail holds the operator evidence) plus the accounting of its last successful tick,
/// read from the slot's <c>outbox-status</c> variable: <c>LastTickSummary</c>, <c>Backlog</c> (pending rows still awaiting relay), and
/// <c>QuarantineTotal</c>, all null until the slot's first successful tick. <c>IsLocal</c> says this process
/// registered the source, so <see cref="IOutbox.ListQuarantinedAsync"/> works here.
/// </summary>
/// <param name="JobNamespace">The namespace whose outbox relay this is.</param>
/// <param name="SlotJobRef">The ref of the permanent slot job that runs the namespace's outbox relay.</param>
/// <param name="LastTickSummary">A summary of the relay's last pass; null until its first successful pass.</param>
/// <param name="Backlog">How many rows are still waiting to be relayed; null until the first successful pass.</param>
/// <param name="QuarantineTotal">How many rows the relay has quarantined; null until the first successful pass.</param>
/// <param name="IsLocal">Whether this process relays the source.</param>
public sealed record ListOutboxSourcesItem(
    string JobNamespace,
    JobRef SlotJobRef,
    string? LastTickSummary,
    long? Backlog,
    long? QuarantineTotal,
    bool IsLocal
);
