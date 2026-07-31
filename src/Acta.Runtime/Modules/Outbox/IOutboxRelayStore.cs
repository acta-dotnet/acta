namespace Acta.Runtime.Modules.Outbox;

/// <summary>
/// Persistence port for the external-outbox source database, owned by the relay (<c>sys.outbox</c>).
/// Independent of the Acta-ledger <c>IDbSession</c>: a worker may relay a source database on a
/// different provider than its own ledger. Every claim/finalize update is token-CAS so a stale
/// execution cannot finalize work reclaimed by another relay.
/// </summary>
internal interface IOutboxRelayStore
{
    /// <summary>
    /// One short source transaction: return expired Claimed leases to Pending, then claim a bounded
    /// batch of due Pending rows (<c>status_code = 10 AND next_attempt_at_utc &lt;= db_now</c>) ordered by
    /// <c>COALESCE(priority_code,50) DESC, next_attempt_at_utc ASC, created_at_utc ASC, outbox_id ASC</c>,
    /// stamping <c>claim_token</c>/<c>claim_until_utc</c>. Returns the claimed rows for this token.
    /// </summary>
    Task<IReadOnlyList<OutboxRow>> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Token-CAS delete every claimed row named by the command whose representative safely ingested
    /// (Inserted or Deduplicated). Rows whose token no longer matches are skipped.
    /// </summary>
    Task DeleteClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Token-CAS reschedule of recoverable row-specific failures: set each row's own <c>failure_count</c>,
    /// push <c>next_attempt_at_utc</c> to <c>source_db_now + BackoffSeconds</c> (source-clock anchored),
    /// record <c>last_error</c>, and clear the claim pair to Pending.
    /// </summary>
    Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Token-CAS transition of rows to Quarantined (90), clearing the claim pair and recording each row's
    /// own consumed <c>failure_count</c> and <c>last_error</c>. Quarantined rows are excluded from normal
    /// claims until an operator acts.
    /// </summary>
    Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Best-effort token-CAS release of unprocessed claims back to Pending on cancellation or at the
    /// per-tick bound. Correctness does not depend on this succeeding: lease expiry plus token-CAS
    /// finalization make an abandoned claim safe to repeat.
    /// </summary>
    Task ReleaseClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct);

    /// <summary>
    /// Counts the source's Pending rows (due or backed off; Claimed and Quarantined rows are excluded).
    /// Read after finalization so each successful tick's summary reports what still awaits relay.
    /// </summary>
    Task<long> CountBacklogAsync(CancellationToken ct);
}
