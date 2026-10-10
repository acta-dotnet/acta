namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="IOutbox"/> with one known source, "billing": local, relaying for the found job, with one quarantined row.
/// A quarantine listing outside "billing" throws the non-local error like production. The verbs accept, except that a
/// namespace of "missing" reports not found and "rejected" a pending-command rejection.
/// </summary>
internal sealed class FakeOutbox : IOutbox
{
    public static readonly Guid QuarantinedId = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    public static readonly DateTime PendingSince = new(2026, 6, 12, 7, 30, 0, DateTimeKind.Utc);

    public FakeOutbox()
    {
        ListSources = static (query, _) =>
            ValueTask.FromResult(
                new ListOutboxSourcesResponse(query.JobNamespace is null or "billing" ? [Billing] : [], null, false, 50, null)
            );
        ListQuarantined = static (query, _) => ValueTask.FromResult(Quarantined(query));
        Requeue = (jobNamespace, outboxIds, reason, actor, _) =>
            Control(
                "requeue",
                jobNamespace,
                outboxIds,
                reason,
                actor,
                static (action, pendingSince) => new RequeueOutboxResponse(action, pendingSince)
            );
        Discard = (jobNamespace, outboxIds, reason, actor, _) =>
            Control(
                "discard",
                jobNamespace,
                outboxIds,
                reason,
                actor,
                static (action, pendingSince) => new DiscardOutboxResponse(action, pendingSince)
            );
    }

    /// <summary>Accepted requeue and discard calls with their scope and audit fields.</summary>
    public List<(string Verb, string JobNamespace, IReadOnlyList<Guid>? OutboxIds, string? Reason, string? Actor)> ControlCalls { get; } =
    [];

    public Func<ListOutboxSourcesQuery, CancellationToken, ValueTask<ListOutboxSourcesResponse>> ListSources { get; set; }

    public Func<ListOutboxQuarantinedQuery, CancellationToken, ValueTask<ListOutboxQuarantinedResponse>> ListQuarantined { get; set; }

    public Func<string, IReadOnlyList<Guid>?, string?, string?, CancellationToken, ValueTask<RequeueOutboxResponse>> Requeue { get; set; }

    public Func<string, IReadOnlyList<Guid>?, string?, string?, CancellationToken, ValueTask<DiscardOutboxResponse>> Discard { get; set; }

    ValueTask<ListOutboxSourcesResponse> IOutbox.ListSourcesAsync(ListOutboxSourcesQuery query, CancellationToken ct) =>
        ListSources(query, ct);

    ValueTask<ListOutboxQuarantinedResponse> IOutbox.ListQuarantinedAsync(ListOutboxQuarantinedQuery query, CancellationToken ct) =>
        ListQuarantined(query, ct);

    ValueTask<RequeueOutboxResponse> IOutbox.RequeueAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Requeue(jobNamespace, outboxIds, reasonMessage, actor, ct);

    ValueTask<DiscardOutboxResponse> IOutbox.DiscardAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Discard(jobNamespace, outboxIds, reasonMessage, actor, ct);

    private static readonly ListOutboxSourcesItem Billing = new(
        "billing",
        FakeJobs.FoundJobRef,
        "claimed=2 relayed=2 dedup=0 quarantined=0 backlog=5 quarantine=1",
        5,
        1,
        IsLocal: true
    );

    private static ListOutboxQuarantinedResponse Quarantined(ListOutboxQuarantinedQuery query) =>
        query.JobNamespace == "billing"
            ? new ListOutboxQuarantinedResponse(
                [
                    new ListOutboxQuarantinedItem(
                        QuarantinedId,
                        "billing",
                        "send-invoice",
                        "inv-9",
                        "trace-9",
                        null,
                        5,
                        "route rejected: unknown job",
                        new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc)
                    ),
                ],
                null,
                false,
                50,
                query.IncludeTotal ? 1L : null
            )
            : throw new InvalidOperationException($"Namespace '{query.JobNamespace}' has no outbox relay registered in this process.");

    private ValueTask<T> Control<T>(
        string verb,
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds,
        string? reasonMessage,
        string? actor,
        Func<ControlAction, DateTime?, T> response
    )
    {
        if (jobNamespace == "missing")
        {
            return ValueTask.FromResult(response(ControlAction.NotFound, null));
        }

        if (jobNamespace == "rejected")
        {
            return ValueTask.FromResult(response(ControlAction.Rejected, PendingSince));
        }

        ControlCalls.Add((verb, jobNamespace, outboxIds, reasonMessage, actor));
        return ValueTask.FromResult(response(ControlAction.Accepted, null));
    }
}
