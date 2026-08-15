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
        AlertRef alertRef,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        var outcome = await store.AcknowledgeJobAlertAsync(
            new AlertControlCommand(alertRef.Value, Operator(actorKey), Reason(alertRef, reasonMessage)),
            ct
        );
        return ToResult(alertRef, outcome);
    }

    public async ValueTask<AlertControlResult> ResolveAsync(
        AlertRef alertRef,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        var outcome = await store.ResolveJobAlertManualAsync(
            new AlertControlCommand(alertRef.Value, Operator(actorKey), Reason(alertRef, reasonMessage)),
            ct
        );
        return ToResult(alertRef, outcome);
    }

    public async ValueTask<AlertDetail?> GetAsync(AlertRef alertRef, CancellationToken ct = default)
    {
        var row = await store.GetJobAlertAsync(alertRef.Value, ct);
        return row is null
            ? null
            : new AlertDetail(
                row.AlertRef,
                row.AlertId,
                row.JobNamespace,
                row.JobId,
                row.JobRef,
                row.Origin,
                row.Severity,
                row.Kind,
                row.Title,
                row.Message,
                row.ChannelName,
                row.OccurrenceCount,
                row.ResolvedAtUtc,
                row.DeliveryStatus,
                row.RetryCount,
                row.RetryAfterUtc,
                row.CreatedAtUtc,
                row.ModifiedAtUtc,
                row.AcknowledgedAtUtc
            );
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
            ("ack", query.AcknowledgedOnly?.ToString()),
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
                query.AcknowledgedOnly,
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
            ? PageCursorCodec.Encode(ListOperationName, OrderCreatedDesc, filterHash, [items[^1].CreatedAtUtc, items[^1].AlertId])
            : null;

        return new PagedResult<AlertListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static string? Num(long? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Reason(AlertRef alertRef, string? reasonMessage) =>
        (reasonMessage is null ? $"alert {alertRef}" : $"alert {alertRef}: {reasonMessage}").Truncate(ActaTextLimits.ReasonMessage)!;

    private static AlertControlResult ToResult(AlertRef alertRef, AlertControlOutcome o) =>
        new(alertRef, (ControlAction)(byte)o.Action, o.AcknowledgedAtUtc, o.ResolvedAtUtc);
}
