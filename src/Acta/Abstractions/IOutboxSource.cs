namespace Acta;

/// <summary>
/// One producer's outbox table as the relay (<c>sys.outbox</c>) and the quarantine listing reach it, over a
/// session and dialect bound to that source database rather than the ledger: a worker may relay a source on
/// another provider. Every claim and finalize is token-CAS, so a stale execution cannot finalize work another
/// relay reclaimed. <see cref="IOutboxOperations.OpenSource"/> opens one over the connection a provider package builds.
/// </summary>
internal interface IOutboxSource
{
    /// <summary>
    /// One short source transaction: expired Claimed leases back to Pending, then a bounded batch of due
    /// Pending rows claimed by next attempt then id under a fresh token, returned in staging order. A laned
    /// row is claimed only while no older row of its lane is Pending or Claimed, so a lane reaches the ledger
    /// in staging order; that check must not skip locked rows, or a row another relay holds would read as gone.
    /// </summary>
    Task<ClaimOutboxResponse> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Deletes every claimed row the command names whose representative safely ingested; a row whose token
    /// moved is skipped.
    /// </summary>
    Task DeleteClaimedAsync(DeleteClaimedOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Returns recoverable failures to Pending: each row's own failure count, <c>next_attempt_at_utc</c> at the
    /// source clock plus its backoff, and its last error.
    /// </summary>
    Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Moves rows to Quarantined with each row's own consumed failure count and last error; quarantined rows
    /// are never claimed until an operator acts.
    /// </summary>
    Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Best-effort release of unprocessed claims back to Pending on cancellation or at the per-tick bound;
    /// lease expiry and token-CAS finalization make an abandoned claim safe either way.
    /// </summary>
    Task ReleaseClaimedAsync(ReleaseClaimedOutboxCommand command, CancellationToken ct);

    /// <summary>The source's Pending rows, due or backed off, read after finalization for the tick summary.</summary>
    Task<long> CountBacklogAsync(CancellationToken ct);

    /// <summary>
    /// The source's Quarantined rows, read with the backlog so the tick summary carries the quarantine total
    /// to peers that cannot reach the producer's database.
    /// </summary>
    Task<long> CountQuarantinedAsync(CancellationToken ct);

    /// <summary>One keyset page of Quarantined rows ordered by outbox id: identity and failure evidence, never the payload.</summary>
    Task<ListQuarantinedOutboxResponse> ListQuarantinedAsync(ListQuarantinedOutboxQuery query, CancellationToken ct);

    /// <summary>
    /// Returns Quarantined rows to Pending, immediately due, failure count reset and last error kept as
    /// evidence. The status filter is the whole guard, since quarantined rows are never claimed.
    /// </summary>
    Task<RequeueQuarantinedOutboxResponse> RequeueQuarantinedAsync(RequeueQuarantinedOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Deletes Quarantined rows and returns their ids, so the applying tick can write them into ledger evidence
    /// before the proof leaves the source; the status filter keeps an in-flight claimed row safe.
    /// </summary>
    Task<DiscardQuarantinedOutboxResponse> DiscardQuarantinedAsync(DiscardQuarantinedOutboxCommand command, CancellationToken ct);
}
