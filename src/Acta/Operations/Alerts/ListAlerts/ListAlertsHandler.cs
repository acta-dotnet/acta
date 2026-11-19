using System.Globalization;

namespace Acta;

/// <summary><see cref="IAlerts.ListAsync"/>: resolves the job filter, then alerts newest first, one keyset page at a time.</summary>
internal sealed class ListAlertsHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async Task<ListAlertsResponse> HandleAsync(ListAlertsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        QueryValidation.ValidateEnum(query.SeverityAtLeast, nameof(query.SeverityAtLeast));
        QueryValidation.ValidateEnum(query.DeliveryStatus, nameof(query.DeliveryStatus));

        // A job that is not there has no alerts: answered before the cursor is read.
        long? jobId = null;
        if (query.Job is { } job && (jobId = (await getJobId.HandleAsync(new GetJobIdQuery(job), ct))?.JobId) is null)
        {
            return new ListAlertsResponse(
                [],
                null,
                false,
                JobsQueryLimits.NormalizePageSize(query.PageSize),
                query.IncludeTotal ? 0 : null
            );
        }

        var unresolvedOnly = query.UnresolvedOnly == true ? true : (bool?)null;
        var paging = Paging.Start(
            "ListAlerts",
            "created_at_utc desc, id desc",
            [CursorKeyKind.Utc, CursorKeyKind.Long],
            query.Cursor,
            query.PageSize,
            [
                ("ns", jobNamespace),
                ("job", jobId?.ToString(CultureInfo.InvariantCulture)),
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
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.JobId, jobId));
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
