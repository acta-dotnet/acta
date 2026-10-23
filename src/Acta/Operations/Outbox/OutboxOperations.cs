namespace Acta;

/// <summary>
/// The Outbox operations: <see cref="IOutbox"/> for applications, plus the relay's calls. The slot's operator-command inbox
/// is Signal-kind checkpoint rows on the slot job, durable, cross-peer, and bounded by the two fixed names, where
/// operators park commands that the relay tick reads, plans, applies, and consumes with their evidence.
/// </summary>
internal sealed class OutboxOperations(
    ListOutboxSourcesHandler listOutboxSources,
    ListOutboxQuarantinedHandler listOutboxQuarantined,
    RequeueOutboxHandler requeueOutbox,
    DiscardOutboxHandler discardOutbox,
    GetOutboxSignalHandler getOutboxSignal,
    PlanOutboxSignalHandler planOutboxSignal,
    ConsumeOutboxSignalHandler consumeOutboxSignal
) : IOutbox
{
    public ValueTask<ListOutboxSourcesResponse> ListSourcesAsync(ListOutboxSourcesQuery query, CancellationToken ct = default) =>
        listOutboxSources.HandleAsync(query, ct);

    public ValueTask<ListOutboxQuarantinedResponse> ListQuarantinedAsync(
        ListOutboxQuarantinedQuery query,
        CancellationToken ct = default
    ) => listOutboxQuarantined.HandleAsync(query, ct);

    public ValueTask<RequeueOutboxResponse> RequeueAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds = null,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => requeueOutbox.HandleAsync(new RequeueOutboxCommand(jobNamespace, outboxIds, reasonMessage, actor), ct);

    public ValueTask<DiscardOutboxResponse> DiscardAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds = null,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => discardOutbox.HandleAsync(new DiscardOutboxCommand(jobNamespace, outboxIds, reasonMessage, actor), ct);

    /// <summary>Reads the slot's pending command under <paramref name="name"/>, or null when that inbox slot is empty.</summary>
    internal Task<GetOutboxSignalResponse?> GetOutboxSignalAsync(long slotJobId, string name, CancellationToken ct) =>
        getOutboxSignal.HandleAsync(new GetOutboxSignalQuery(slotJobId, name), ct);

    /// <summary>
    /// Replaces the command row whose stored value is still the <paramref name="value"/> the tick read with its plan,
    /// <paramref name="plannedValue"/>. A pass whose read no longer matches stores nothing and must not touch the source.
    /// </summary>
    internal Task<PlanOutboxSignalResponse> PlanOutboxSignalAsync(
        long slotJobId,
        string name,
        byte[] value,
        byte[] plannedValue,
        CancellationToken ct
    ) => planOutboxSignal.HandleAsync(new PlanOutboxSignalCommand(slotJobId, name, value, plannedValue), ct);

    /// <summary>
    /// Deletes an applied command row whose stored value is still the one the tick read and, only then, writes its
    /// always-audited evidence event. The value carries the command id minted per park, so a command that superseded
    /// it, or was parked after another pass consumed it, is not consumed and applies on the next tick.
    /// </summary>
    internal Task<ConsumeOutboxSignalResponse> ConsumeOutboxSignalAsync(ConsumeOutboxSignalCommand command, CancellationToken ct) =>
        consumeOutboxSignal.HandleAsync(command, ct);

    /// <summary>Opens a producer's outbox table over the connection its provider package built; opening contacts no database.</summary>
    internal static Source OpenSource(OutboxSourceConnection connection) => new(connection.Session, connection.Dialect);

    /// <summary>
    /// One producer's outbox table as the relay (<c>sys.outbox</c>) and the quarantine listing reach it, over a
    /// session and dialect bound to that source database rather than the ledger: a worker may relay a source on
    /// another provider. Every claim and finalize is token-CAS, so a stale execution cannot finalize work another
    /// relay reclaimed. <see cref="OpenSource"/> opens one over the connection a provider package builds.
    /// </summary>
    internal sealed class Source(IDbSession session, ISqlDialect dialect)
    {
        private readonly ClaimOutboxHandler _claim = new(session, dialect);
        private readonly DeleteClaimedOutboxHandler _deleteClaimed = new(session, dialect);
        private readonly RescheduleOutboxHandler _reschedule = new(session, dialect);
        private readonly QuarantineOutboxHandler _quarantine = new(session, dialect);
        private readonly ReleaseClaimedOutboxHandler _releaseClaimed = new(session, dialect);
        private readonly CountOutboxBacklogHandler _countBacklog = new(session);
        private readonly CountOutboxQuarantinedHandler _countQuarantined = new(session);
        private readonly ListQuarantinedOutboxHandler _listQuarantined = new(session, dialect);
        private readonly RequeueQuarantinedOutboxHandler _requeueQuarantined = new(session, dialect);
        private readonly DiscardQuarantinedOutboxHandler _discardQuarantined = new(session, dialect);
        private readonly MatchQuarantinedOutboxHandler _matchQuarantined = new(session, dialect);

        /// <summary>
        /// One short source transaction: expired Claimed leases back to Pending, then a bounded batch of due
        /// Pending rows claimed by next attempt then id under a fresh token, returned in staging order. A laned
        /// row is claimed only while no older row of its lane is Pending or Claimed, so a lane reaches the ledger
        /// in staging order; that check must not skip locked rows, or a row another relay holds would read as gone.
        /// </summary>
        public Task<ClaimOutboxResponse> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct) => _claim.HandleAsync(command, ct);

        /// <summary>
        /// Deletes every claimed row the command names whose representative safely ingested; a row whose token
        /// moved is skipped.
        /// </summary>
        public Task DeleteClaimedAsync(DeleteClaimedOutboxCommand command, CancellationToken ct) => _deleteClaimed.HandleAsync(command, ct);

        /// <summary>
        /// Returns recoverable failures to Pending: each row's own failure count, <c>next_attempt_at_utc</c> at the
        /// source clock plus its backoff, and its last error.
        /// </summary>
        public Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct) => _reschedule.HandleAsync(command, ct);

        /// <summary>
        /// Moves rows to Quarantined with each row's own consumed failure count and last error; quarantined rows
        /// are never claimed until an operator acts.
        /// </summary>
        public Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct) => _quarantine.HandleAsync(command, ct);

        /// <summary>
        /// Best-effort release of unprocessed claims back to Pending on cancellation or at the per-tick bound;
        /// lease expiry and token-CAS finalization make an abandoned claim safe either way.
        /// </summary>
        public Task ReleaseClaimedAsync(ReleaseClaimedOutboxCommand command, CancellationToken ct) =>
            _releaseClaimed.HandleAsync(command, ct);

        /// <summary>The source's Pending rows, due or backed off, read after finalization for the tick summary.</summary>
        public Task<long> CountBacklogAsync(CancellationToken ct) => _countBacklog.HandleAsync(ct);

        /// <summary>
        /// The source's Quarantined rows, read with the backlog so the tick summary carries the quarantine total
        /// to peers that cannot reach the producer's database.
        /// </summary>
        public Task<long> CountQuarantinedAsync(CancellationToken ct) => _countQuarantined.HandleAsync(ct);

        /// <summary>One keyset page of Quarantined rows ordered by outbox id: identity and failure evidence, never the payload.</summary>
        public Task<ListQuarantinedOutboxResponse> ListQuarantinedAsync(ListQuarantinedOutboxQuery query, CancellationToken ct) =>
            _listQuarantined.HandleAsync(query, ct);

        /// <summary>
        /// Returns Quarantined rows to Pending, immediately due, failure count reset and last error kept as
        /// evidence. The status filter is the whole guard, since quarantined rows are never claimed.
        /// </summary>
        public Task<RequeueQuarantinedOutboxResponse> RequeueQuarantinedAsync(
            RequeueQuarantinedOutboxCommand command,
            CancellationToken ct
        ) => _requeueQuarantined.HandleAsync(command, ct);

        /// <summary>
        /// Deletes Quarantined rows and returns their ids, so the applying tick can write them into ledger evidence
        /// before the proof leaves the source; the status filter keeps an in-flight claimed row safe.
        /// </summary>
        public Task<DiscardQuarantinedOutboxResponse> DiscardQuarantinedAsync(
            DiscardQuarantinedOutboxCommand command,
            CancellationToken ct
        ) => _discardQuarantined.HandleAsync(command, ct);

        /// <summary>
        /// The ids of the Quarantined rows an operator command targets, read without changing any, so the applying
        /// tick can record them as the command's plan before it requeues or discards.
        /// </summary>
        public Task<MatchQuarantinedOutboxResponse> MatchQuarantinedAsync(MatchQuarantinedOutboxQuery query, CancellationToken ct) =>
            _matchQuarantined.HandleAsync(query, ct);
    }
}
