namespace Acta;

/// <summary><see cref="IOutbox"/>, one line per operation.</summary>
internal sealed class Outbox(
    ListOutboxSourcesHandler listOutboxSources,
    ListOutboxQuarantinedHandler listOutboxQuarantined,
    RequeueOutboxHandler requeueOutbox,
    DiscardOutboxHandler discardOutbox
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
}
