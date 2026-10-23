using System.Globalization;

namespace Acta;

/// <summary><see cref="IAlerts.ListAsync"/>: alerts newest first, one keyset page at a time.</summary>
internal sealed class ListAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListAlertsResponse> HandleAsync(ListAlertsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        QueryValidation.ValidateEnum(query.SeverityAtLeast, nameof(query.SeverityAtLeast));
        QueryValidation.ValidateEnum(query.DeliveryStatus, nameof(query.DeliveryStatus));
        QueryValidation.ValidatePositiveId(query.JobId, nameof(query.JobId));

        var unresolvedOnly = query.UnresolvedOnly == true ? true : (bool?)null;
        var paging = Paging.Start(
            "ListAlerts",
            "created_at_utc desc, id desc",
            [CursorKeyKind.Utc, CursorKeyKind.Long],
            query.Cursor,
            query.PageSize,
            [
                ("ns", jobNamespace),
                ("job", query.JobId?.ToString(CultureInfo.InvariantCulture)),
                ("open", unresolvedOnly?.ToString()),
                ("sev", ((byte?)query.SeverityAtLeast)?.ToString(CultureInfo.InvariantCulture)),
                ("delivery", ((byte?)query.DeliveryStatus)?.ToString(CultureInfo.InvariantCulture)),
                ("ack", query.AcknowledgedOnly?.ToString()),
            ]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListAlerts",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.JobId, query.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.UnresolvedOnlyFlag, unresolvedOnly));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.AlertSeverityCode, query.SeverityAtLeast));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.DeliveryStatusCode, query.DeliveryStatus));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AcknowledgedFilter, query.AcknowledgedOnly));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, (long?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListAlertsItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.CreatedAtUtc, item.AlertId]);
        return new ListAlertsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
