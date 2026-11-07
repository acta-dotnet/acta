namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="IOutbox"/> with one known source, "billing": local, relayed by its own permanent slot job, with one quarantined row.
/// A quarantine listing outside "billing" throws the non-local error like production. The verbs accept, except that a name
/// that is no namespace name throws the name validation error production throws, a namespace of "missing" reports not
/// found, and "rejected" a pending-command rejection.
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
                false,
                reason,
                actor,
                static (action, pendingSince) => new RequeueOutboxResponse(action, pendingSince)
            );
        Discard = (jobNamespace, outboxIds, all, reason, actor, _) =>
            Control(
                "discard",
                jobNamespace,
                outboxIds,
                all,
                reason,
                actor,
                static (action, pendingSince) => new DiscardOutboxResponse(action, pendingSince)
            );
    }

    /// <summary>Accepted requeue and discard calls with their scope and audit fields; a requeue's <c>All</c> is false.</summary>
    public List<(
        string Verb,
        string JobNamespace,
        IReadOnlyList<Guid>? OutboxIds,
        bool All,
        string? Reason,
        string? Actor
    )> ControlCalls { get; } = [];

    public Func<ListOutboxSourcesQuery, CancellationToken, ValueTask<ListOutboxSourcesResponse>> ListSources { get; set; }

    public Func<ListQuarantinedOutboxQuery, CancellationToken, ValueTask<ListQuarantinedOutboxResponse>> ListQuarantined { get; set; }

    public Func<string, IReadOnlyList<Guid>?, string?, string?, CancellationToken, ValueTask<RequeueOutboxResponse>> Requeue { get; set; }

    public Func<
        string,
        IReadOnlyList<Guid>?,
        bool,
        string?,
        string?,
        CancellationToken,
        ValueTask<DiscardOutboxResponse>
    > Discard { get; set; }

    ValueTask<ListOutboxSourcesResponse> IOutbox.ListSourcesAsync(ListOutboxSourcesQuery query, CancellationToken ct) =>
        ListSources(query, ct);

    ValueTask<ListQuarantinedOutboxResponse> IOutbox.ListQuarantinedAsync(ListQuarantinedOutboxQuery query, CancellationToken ct) =>
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
        bool all,
        string? reasonMessage,
        string? actor,
        CancellationToken ct
    ) => Discard(jobNamespace, outboxIds, all, reasonMessage, actor, ct);

    private static readonly ListOutboxSourcesItem Billing = new(
        "billing",
        new JobRef(Guid.Parse("00000000-0000-0000-0000-000000000044")),
        "claimed=2 relayed=2 dedup=0 quarantined=0 backlog=5 quarantine=1",
        5,
        1,
        IsLocal: true
    );

    private static ListQuarantinedOutboxResponse Quarantined(ListQuarantinedOutboxQuery query) =>
        query.JobNamespace == "billing"
            ? new ListQuarantinedOutboxResponse(
                [
                    new ListQuarantinedOutboxItem(
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
        bool all,
        string? reasonMessage,
        string? actor,
        Func<ControlAction, DateTime?, T> response
    )
    {
        _ = NameSyntax.Canonicalize(jobNamespace, nameof(jobNamespace));
        if (jobNamespace == "missing")
        {
            return ValueTask.FromResult(response(ControlAction.NotFound, null));
        }

        if (jobNamespace == "rejected")
        {
            return ValueTask.FromResult(response(ControlAction.Rejected, PendingSince));
        }

        ControlCalls.Add((verb, jobNamespace, outboxIds, all, reasonMessage, actor));
        return ValueTask.FromResult(response(ControlAction.Accepted, null));
    }
}
