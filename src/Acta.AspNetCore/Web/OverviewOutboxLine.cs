namespace Acta.AspNetCore.Web;

/// <summary>
/// One namespace's external-outbox lag line for the overview health verdict: the <c>sys.outbox</c>
/// slot's persisted tick summary (<c>claimed=.. backlog=N</c>) with the backlog parsed out. Composed
/// from ledger reads only; the dashboard never opens producer databases, so the relay's own last
/// successful tick is the source of truth for source lag.
/// </summary>
internal sealed record OverviewOutboxLine(string JobNamespace, string JobRef, string Tick, long Backlog);
