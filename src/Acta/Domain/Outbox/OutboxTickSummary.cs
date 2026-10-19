namespace Acta;

/// <summary>
/// One relay tick's accounting, written to the <c>sys.outbox</c> slot's <see cref="VariableName"/> variable after each
/// successful tick: source rows claimed, new jobs inserted, rows absorbed by deduplication (coalesced members plus
/// already-present handoffs), rows quarantined (zero on every stored summary, since a quarantine fails the tick), the
/// source's remaining Pending backlog, and the Quarantined total. Peers without this source registration read it there.
/// </summary>
internal sealed record OutboxTickSummary(
    int Claimed,
    int Relayed,
    int Deduplicated,
    int Quarantined,
    long Backlog,
    long QuarantineTotal,
    int Requeued = 0,
    int Discarded = 0
)
{
    public const string VariableName = "outbox-status";

    public override string ToString() =>
        $"claimed={Claimed} relayed={Relayed} dedup={Deduplicated} quarantined={Quarantined} backlog={Backlog} quarantine={QuarantineTotal}"
        + (Requeued > 0 ? $" requeued={Requeued}" : "")
        + (Discarded > 0 ? $" discarded={Discarded}" : "");
}
