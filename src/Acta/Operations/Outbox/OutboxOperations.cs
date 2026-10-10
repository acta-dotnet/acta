namespace Acta;

/// <summary><see cref="IOutboxOperations"/>, one line per operation.</summary>
internal sealed class OutboxOperations(
    ListOutboxSourcesHandler listOutboxSources,
    ListOutboxQuarantinedHandler listOutboxQuarantined,
    RequeueOutboxHandler requeueOutbox,
    DiscardOutboxHandler discardOutbox,
    GetOutboxSignalHandler getOutboxSignal,
    ConsumeOutboxSignalHandler consumeOutboxSignal,
    RecordOutboxEventHandler recordOutboxEvent
) : IOutboxOperations
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
        string? actorKey = null,
        CancellationToken ct = default
    ) => requeueOutbox.HandleAsync(new RequeueOutboxCommand(jobNamespace, outboxIds, reasonMessage, actorKey), ct);

    public ValueTask<DiscardOutboxResponse> DiscardAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => discardOutbox.HandleAsync(new DiscardOutboxCommand(jobNamespace, outboxIds, reasonMessage, actorKey), ct);

    public Task<GetOutboxSignalResponse?> GetOutboxSignalAsync(long slotJobId, string name, CancellationToken ct) =>
        getOutboxSignal.HandleAsync(new GetOutboxSignalQuery(slotJobId, name), ct);

    public Task<ConsumeOutboxSignalResponse> ConsumeOutboxSignalAsync(long slotJobId, string name, int version, CancellationToken ct) =>
        consumeOutboxSignal.HandleAsync(new ConsumeOutboxSignalCommand(slotJobId, name, version), ct);

    public Task RecordOutboxEventAsync(RecordOutboxEventCommand command, CancellationToken ct) =>
        recordOutboxEvent.HandleAsync(command, ct);

    public IOutboxSource OpenSource(OutboxSourceConnection connection) => new Source(connection.Session, connection.Dialect);

    /// <summary><see cref="IOutboxSource"/> over the source's own session and dialect, one line per operation.</summary>
    private sealed class Source(IDbSession session, ISqlDialect dialect) : IOutboxSource
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

        public Task<ClaimOutboxResponse> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct) => _claim.HandleAsync(command, ct);

        public Task DeleteClaimedAsync(DeleteClaimedOutboxCommand command, CancellationToken ct) => _deleteClaimed.HandleAsync(command, ct);

        public Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct) => _reschedule.HandleAsync(command, ct);

        public Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct) => _quarantine.HandleAsync(command, ct);

        public Task ReleaseClaimedAsync(ReleaseClaimedOutboxCommand command, CancellationToken ct) =>
            _releaseClaimed.HandleAsync(command, ct);

        public Task<long> CountBacklogAsync(CancellationToken ct) => _countBacklog.HandleAsync(ct);

        public Task<long> CountQuarantinedAsync(CancellationToken ct) => _countQuarantined.HandleAsync(ct);

        public Task<ListQuarantinedOutboxResponse> ListQuarantinedAsync(ListQuarantinedOutboxQuery query, CancellationToken ct) =>
            _listQuarantined.HandleAsync(query, ct);

        public Task<RequeueQuarantinedOutboxResponse> RequeueQuarantinedAsync(
            RequeueQuarantinedOutboxCommand command,
            CancellationToken ct
        ) => _requeueQuarantined.HandleAsync(command, ct);

        public Task<DiscardQuarantinedOutboxResponse> DiscardQuarantinedAsync(
            DiscardQuarantinedOutboxCommand command,
            CancellationToken ct
        ) => _discardQuarantined.HandleAsync(command, ct);
    }
}
