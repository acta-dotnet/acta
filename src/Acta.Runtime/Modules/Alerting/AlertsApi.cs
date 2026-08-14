using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Querying;

namespace Acta.Runtime.Modules.Alerting;

/// <summary><see cref="IAlerts"/> implementation: the acknowledge/resolve control verbs plus the paged alert list over the store port.</summary>
internal sealed class AlertsApi(IAlertStore store) : IAlerts
{
    private const string ListOperationName = "ListJobAlerts";
    private const string OrderCreatedDesc = "created_at_utc desc, id desc";

    // The control surface is operator/manual only: the actor (Operator) is stamped here, never accepted
    // from the caller, so a caller cannot forge the audit actor.
    private static JobControlActor Operator(string? actorKey) =>
        new(ActorCode.Operator, JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey));

    public async ValueTask<AlertControlResult> AcknowledgeAsync(
        long alertId,
        string? note = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        var outcome = await store.AcknowledgeJobAlertAsync(new AlertControlCommand(alertId, Operator(actorKey), Reason(alertId, note)), ct);
        return ToResult(alertId, outcome);
    }

    public async ValueTask<AlertControlResult> ResolveAsync(
        long alertId,
        string? note = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        var outcome = await store.ResolveJobAlertManualAsync(
            new AlertControlCommand(alertId, Operator(actorKey), Reason(alertId, note)),
            ct
        );
        return ToResult(alertId, outcome);
    }

    public async ValueTask<PagedResult<AlertListItem>> ListAsync(ListAlertsQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with { JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)) };
        QueryValidation.ValidateEnum(query.SeverityAtLeast, nameof(query.SeverityAtLeast));
        QueryValidation.ValidateEnum(query.DeliveryStatus, nameof(query.DeliveryStatus));
        QueryValidation.ValidatePositiveId(query.JobId, nameof(query.JobId));

        var unresolvedOnly = query.UnresolvedOnly == true ? true : (bool?)null;
        var tagFilters = TagFilterJson.Normalize(query.Tags, nameof(ListAlertsQuery));
        var filterHash = QueryFilterHash.Compute([
            ("ns", query.JobNamespace),
            ("job", Num(query.JobId)),
            ("open", unresolvedOnly?.ToString()),
            ("sev", Num(query.SeverityAtLeast)),
            ("delivery", Num(query.DeliveryStatus)),
            ("ack", query.Acknowledged?.ToString()),
            ("tags", tagFilters),
        ]);

        DateTime? cursorCreatedAtUtc = null;
        long? cursorId = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(
                query.Cursor,
                ListOperationName,
                OrderCreatedDesc,
                filterHash,
                [CursorKeyKind.Utc, CursorKeyKind.Long]
            );
            cursorCreatedAtUtc = (DateTime)keys[0];
            cursorId = (long)keys[1];
        }

        var page = await store.ListJobAlertsAsync(
            new AlertPageRequest(
                query.JobNamespace,
                query.JobId,
                unresolvedOnly,
                query.SeverityAtLeast,
                query.DeliveryStatus,
                query.Acknowledged,
                cursorCreatedAtUtc,
                cursorId,
                pageSize + 1,
                query.IncludeTotal,
                tagFilters
            ),
            ct
        );

        var rows = page.Rows;
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;

        var nextCursor = hasMore
            ? PageCursorCodec.Encode(ListOperationName, OrderCreatedDesc, filterHash, [items[^1].CreatedAtUtc, items[^1].JobAlertId])
            : null;

        return new PagedResult<AlertListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static string? Num(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Reason(long alertId, string? note) =>
        (note is null ? $"alert {alertId}" : $"alert {alertId}: {note}").Truncate(ActaTextLimits.ReasonMessage)!;

    private static AlertControlResult ToResult(long alertId, AlertControlOutcome o) =>
        new(alertId, (JobControlAction)(byte)o.Action, o.AcknowledgedAtUtc, o.ResolvedAtUtc);
}
