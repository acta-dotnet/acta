namespace Acta.Runtime;

/// <summary>
/// <see cref="IOutbox.ListQuarantinedAsync"/>: one keyset page from the producer's source database, so it
/// runs only on a host that registered the namespace's relay (the registry is absent where none did).
/// </summary>
internal sealed class ListOutboxQuarantinedHandler(OutboxRelayRegistry? registry = null)
{
    public async ValueTask<PagedResult<OutboxQuarantinedListItem>> HandleAsync(ListOutboxQuarantinedQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.JobNamespace);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace))!;
        var paging = Paging.Start(
            "ListOutboxQuarantined",
            "outbox_id asc",
            [CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("ns", jobNamespace)]
        );
        var source =
            registry is not null && registry.HasRelay(jobNamespace)
                ? registry.Service(jobNamespace).Source
                : throw new InvalidOperationException(
                    $"Namespace '{jobNamespace}' has no outbox relay registered in this process. Quarantined rows live in the "
                        + "producer's source database, which only a host that called AddOutboxRelay for this namespace can read; "
                        + "run the listing there (sources report IsLocal), or requeue/discard from any peer."
                );

        Guid? afterOutboxId = null;
        if (paging.After is [string key])
        {
            if (!Guid.TryParse(key, out var parsed))
            {
                throw new InvalidPageCursorException("Cursor key is not a valid outbox id.");
            }
            afterOutboxId = parsed;
        }

        var rows = await source.ListQuarantinedAsync(new ListQuarantinedOutboxCommand(paging.Fetch, afterOutboxId), ct);
        long? total = query.IncludeTotal ? await source.CountQuarantinedAsync(ct) : null;
        return paging.Page(rows, total, static item => [item.OutboxId.ToString("D")]);
    }
}
