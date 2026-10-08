namespace Acta;

/// <summary>
/// One relay source as <see cref="IOutbox.ListSourcesAsync"/> reports it: the namespace's <c>sys.outbox</c> slot
/// (<c>SlotJobRef</c>, whose audit trail holds the operator evidence) plus the accounting parsed from the slot's
/// persisted tick summary: <c>LastTickSummary</c> verbatim, <c>Backlog</c> (pending rows still awaiting relay), and
/// <c>QuarantineTotal</c>, all null until the slot's first successful tick. <c>IsLocal</c> says this process
/// registered the source, so <see cref="IOutbox.ListQuarantinedAsync"/> works here.
/// </summary>
public sealed record ListOutboxSourcesItem(
    string JobNamespace,
    JobRef SlotJobRef,
    string? LastTickSummary,
    long? Backlog,
    long? QuarantineTotal,
    bool IsLocal
);
