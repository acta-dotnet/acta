using System.Text;
using System.Text.Json;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Querying;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Outbox;

/// <summary>
/// <see cref="IOutbox"/> implementation. Reads never open the source database except the quarantine
/// listing (host-local by design: the source store exists only where <c>AddOutboxRelay</c> ran);
/// sources compose cross-peer from each namespace's <c>sys.outbox</c> slot result, and the control
/// verbs park commands on the slot's signal inbox for the next relay pass to apply. The registry is
/// optional because it is registered only in processes that declared a relay.
/// </summary>
internal sealed class OutboxApi(
    IJobs jobs,
    INamespaces namespaces,
    IOutboxSignalStore signals,
    IActaClock clock,
    IOptions<JobsOptions> options,
    OutboxRelayRegistry? registry = null
) : IOutbox
{
    private const string SlotJobName = "sys.outbox";
    private const string ListQuarantinedOperationName = "ListOutboxQuarantined";
    private const string OrderQuarantined = "outbox_id asc";

    public async ValueTask<PagedResult<OutboxSourceListItem>> ListSourcesAsync(ListOutboxSourcesQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));

        IReadOnlyList<string> names;
        string? nextCursor = null;
        var hasMore = false;
        if (jobNamespace is not null)
        {
            names = [jobNamespace];
        }
        else
        {
            var page = await namespaces.ListNamesAsync(new ListNamespacesQuery(PageSize: pageSize, Cursor: query.Cursor), ct);
            (names, nextCursor, hasMore) = (page.Items, page.NextCursor, page.HasMore);
        }

        var items = new List<OutboxSourceListItem>();
        foreach (var name in names)
        {
            if (await ComposeSourceAsync(name, ct) is { } item)
            {
                items.Add(item);
            }
        }

        return new PagedResult<OutboxSourceListItem>(items, nextCursor, hasMore, pageSize, null);
    }

    public async ValueTask<PagedResult<OutboxQuarantinedItem>> ListQuarantinedAsync(
        ListOutboxQuarantinedQuery query,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.JobNamespace);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace))!;
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        var store = ResolveLocalStore(jobNamespace);

        var filterHash = QueryFilterHash.Compute([("ns", jobNamespace)]);
        Guid? afterOutboxId = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(
                query.Cursor,
                ListQuarantinedOperationName,
                OrderQuarantined,
                filterHash,
                [CursorKeyKind.Text]
            );
            if (!Guid.TryParse((string)keys[0], out var parsed))
            {
                throw new InvalidPageCursorException("Cursor key is not a valid outbox id.");
            }
            afterOutboxId = parsed;
        }

        var rows = await store.ListQuarantinedAsync(new ListQuarantinedOutboxCommand(pageSize + 1, afterOutboxId), ct);
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;
        var nextCursor = hasMore
            ? PageCursorCodec.Encode(ListQuarantinedOperationName, OrderQuarantined, filterHash, [items[^1].OutboxId.ToString("D")])
            : null;
        long? total = query.IncludeTotal ? await store.CountQuarantinedAsync(ct) : null;

        return new PagedResult<OutboxQuarantinedItem>(
            [
                .. items.Select(r => new OutboxQuarantinedItem(
                    r.OutboxId,
                    r.JobNamespace,
                    r.JobName,
                    r.DeduplicationKey,
                    r.CorrelationKey,
                    r.TenantKey,
                    r.FailureCount,
                    r.LastError,
                    r.CreatedAtUtc
                )),
            ],
            nextCursor,
            hasMore,
            pageSize,
            total
        );
    }

    public ValueTask<OutboxControlResult> RequeueAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => ParkAsync(OutboxSignalNames.Requeue, jobNamespace, outboxIds, reasonMessage, actorKey, ct);

    public ValueTask<OutboxControlResult> DiscardAsync(
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => ParkAsync(OutboxSignalNames.Discard, jobNamespace, outboxIds, reasonMessage, actorKey, ct);

    private async ValueTask<OutboxControlResult> ParkAsync(
        string name,
        string jobNamespace,
        IReadOnlyList<Guid>? outboxIds,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(jobNamespace);
        jobNamespace = IdentifierSyntax.CanonicalizeKebab(jobNamespace, nameof(jobNamespace));
        if (outboxIds is { Count: 0 })
        {
            throw new ArgumentException("OutboxIds must be null (every quarantined row) or non-empty.", nameof(outboxIds));
        }

        if (await ResolveSlotJobIdAsync(jobNamespace, ct) is not { } slotJobId)
        {
            return new OutboxControlResult(ControlAction.NotFound, null);
        }

        // The minted CommandId is what distinguishes "my park landed" from "a concurrent command
        // landed" in the admission read; the actor rides the payload so the applying tick can stamp
        // the evidence event with the operator identity captured at park time.
        var payload = new OutboxSignalPayload(
            Guid.NewGuid(),
            JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey),
            reasonMessage.Truncate(ActaTextLimits.ReasonMessage),
            outboxIds
        );
        var nowUtc = await clock.GetUtcNowAsync(ct);
        var admission = await signals.ParkAsync(
            new ParkOutboxSignalCommand(
                slotJobId,
                name,
                JobPayloadFormat.Json.Id,
                JsonSerializer.SerializeToUtf8Bytes(payload, OutboxSignalJsonContext.Default.OutboxSignalPayload),
                StaleBeforeUtc: nowUtc - options.Value.WorkerDeadAfter
            ),
            ct
        );

        return admission.Action == (byte)ControlAction.Applied
            ? new OutboxControlResult(ControlAction.Accepted, null)
            : new OutboxControlResult(ControlAction.Rejected, admission.PendingSinceUtc);
    }

    // The slot resolves by its fixed deduplication key, gated on the job name so a user job that
    // happens to reuse the key can never receive operator commands or spoof a source line.
    private async ValueTask<long?> ResolveSlotJobIdAsync(string jobNamespace, CancellationToken ct) =>
        await jobs.GetAsync(JobLookup.ByDeduplicationKey(jobNamespace, SlotJobName), ct) is { JobName: SlotJobName } slot
            ? slot.JobId
            : null;

    private async ValueTask<OutboxSourceListItem?> ComposeSourceAsync(string jobNamespace, CancellationToken ct)
    {
        var slot = await jobs.GetAsync(JobLookup.ByDeduplicationKey(jobNamespace, SlotJobName), ct);
        if (slot is not { JobName: SlotJobName })
        {
            return null;
        }

        var result = await jobs.GetResultAsync(JobLookup.ById(slot.JobId), ct);
        var tick =
            result is { } payload && payload.Format.Id == JobPayloadFormat.Text.Id ? Encoding.UTF8.GetString(payload.Data.Span) : null;

        return new OutboxSourceListItem(
            jobNamespace,
            slot.JobRef,
            tick,
            ParseToken(tick, "backlog="),
            ParseToken(tick, "quarantine="),
            registry?.HasRelay(jobNamespace) == true
        );
    }

    private IOutboxRelayStore ResolveLocalStore(string jobNamespace) =>
        registry is not null && registry.HasRelay(jobNamespace)
            ? registry.Service(jobNamespace).Store
            : throw new InvalidOperationException(
                $"Namespace '{jobNamespace}' has no outbox relay registered in this process. Quarantined rows live in the "
                    + "producer's source database, which only a host that called AddOutboxRelay for this namespace can read; "
                    + "run the listing there (sources report IsLocal), or requeue/discard from any peer."
            );

    // Reads the summary's last "<token>N" value up to the next space, so trailing tokens don't spoil
    // the parse. Null means the token is absent (an older summary format): unknown, not zero.
    internal static long? ParseToken(string? tick, string token)
    {
        var index = tick?.LastIndexOf(token, StringComparison.Ordinal) ?? -1;
        if (index < 0)
        {
            return null;
        }

        var span = tick!.AsSpan(index + token.Length);
        var end = span.IndexOf(' ');
        return long.TryParse(end >= 0 ? span[..end] : span, out var value) ? value : null;
    }
}
