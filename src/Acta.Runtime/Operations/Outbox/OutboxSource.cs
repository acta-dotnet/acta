namespace Acta.Runtime;

/// <summary>
/// <see cref="IOutboxSource"/> over a session and dialect bound to one producer's database, one line per
/// operation. The provider packages build it, with the source connection and the outbox table.
/// </summary>
internal sealed class OutboxSource(IDbSession session, ISqlDialect dialect) : IOutboxSource
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

    public Task<IReadOnlyList<OutboxRow>> ClaimDueAsync(ClaimOutboxCommand command, CancellationToken ct) =>
        _claim.HandleAsync(command, ct);

    public Task DeleteClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct) => _deleteClaimed.HandleAsync(command, ct);

    public Task RescheduleAsync(RescheduleOutboxCommand command, CancellationToken ct) => _reschedule.HandleAsync(command, ct);

    public Task QuarantineAsync(QuarantineOutboxCommand command, CancellationToken ct) => _quarantine.HandleAsync(command, ct);

    public Task ReleaseClaimedAsync(FinalizeOutboxCommand command, CancellationToken ct) => _releaseClaimed.HandleAsync(command, ct);

    public Task<long> CountBacklogAsync(CancellationToken ct) => _countBacklog.HandleAsync(ct);

    public Task<long> CountQuarantinedAsync(CancellationToken ct) => _countQuarantined.HandleAsync(ct);

    public Task<IReadOnlyList<OutboxQuarantinedListItem>> ListQuarantinedAsync(
        ListQuarantinedOutboxCommand command,
        CancellationToken ct
    ) => _listQuarantined.HandleAsync(command, ct);

    public Task<IReadOnlyList<Guid>> RequeueQuarantinedAsync(RequeueQuarantinedOutboxCommand command, CancellationToken ct) =>
        _requeueQuarantined.HandleAsync(command, ct);

    public Task<IReadOnlyList<Guid>> DiscardQuarantinedAsync(DiscardQuarantinedOutboxCommand command, CancellationToken ct) =>
        _discardQuarantined.HandleAsync(command, ct);
}
