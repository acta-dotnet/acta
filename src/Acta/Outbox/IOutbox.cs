namespace Acta;

/// <summary>
/// External-outbox domain: the operator surface over the producer-owned staging tables that
/// <c>sys.outbox</c> relays into the ledger. Reads compose from each namespace's slot job (sources) or
/// from the source database itself (quarantine listing, host-local); the control verbs park durable
/// commands on the slot's signal inbox, applied by the next relay pass. Reached through
/// <see cref="IActaOperations.Outbox"/>.
/// </summary>
public interface IOutbox
{
    /// <summary>Lists the relay sources: one item per namespace whose <c>sys.outbox</c> slot exists, composed cross-peer from the slot's persisted tick summary (any host with ledger access can serve it). A namespace whose slot has produced no successful tick yet reports a null summary and null counters. Paging follows the namespace catalog; a <see cref="ListOutboxSourcesQuery.JobNamespace"/> filter returns at most one item.</summary>
    ValueTask<PagedResult<OutboxSourceListItem>> ListSourcesAsync(ListOutboxSourcesQuery query, CancellationToken ct = default);

    /// <summary>Lists a source's Quarantined rows (identity and failure evidence, never the payload), ordered by outbox id. This read opens the producer's source database, so it works only on a host that registered the namespace's relay via <c>AddOutboxRelay</c>; elsewhere it throws <see cref="InvalidOperationException"/> naming the constraint. Use <see cref="OutboxSourceListItem.IsLocal"/> to discover where the listing can run.</summary>
    ValueTask<PagedResult<OutboxQuarantinedItem>> ListQuarantinedAsync(ListOutboxQuarantinedQuery query, CancellationToken ct = default);

    /// <summary>Requeues Quarantined rows of <paramref name="jobNamespace"/>'s source: back to Pending, immediately due, failure budget reset. Null <paramref name="outboxIds"/> targets every quarantined row. The command parks on the slot's bounded signal inbox and the next relay pass applies it, writing an audit event stamped actor=Operator with <paramref name="actorKey"/> and <paramref name="reasonMessage"/>; any peer can park, no source registration needed. Accepted means parked, with application owed to that pass; a pending unapplied command of the same verb is Rejected carrying its park instant; a namespace without a <c>sys.outbox</c> slot is NotFound.</summary>
    ValueTask<OutboxControlResult> RequeueAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Discards Quarantined rows of <paramref name="jobNamespace"/>'s source: the rows are deleted from the staging table, with the applied ids preserved in the slot job's audit event. Null <paramref name="outboxIds"/> targets every quarantined row. Parking, application, auditing, and outcomes follow <see cref="RequeueAsync"/> exactly.</summary>
    ValueTask<OutboxControlResult> DiscardAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );
}
