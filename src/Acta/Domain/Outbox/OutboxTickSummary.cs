namespace Acta;

/// <summary>
/// One relay tick's accounting, written to the <c>sys.outbox</c> slot's <see cref="VariableName"/> variable after each
/// successful tick: source rows claimed, new jobs inserted, rows absorbed by deduplication (coalesced members plus
/// already-present handoffs), the source's remaining Pending backlog, and the Quarantined total. A tick that
/// quarantines a row fails and stores nothing. Peers without this source registration read it there.
/// </summary>
internal sealed record OutboxTickSummary(
    int Claimed,
    int Relayed,
    int Deduplicated,
    long Backlog,
    long QuarantineTotal,
    int Requeued = 0,
    int Discarded = 0
)
{
    public const string VariableName = "outbox-status";
}
