using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="IAlerts.ListAsync"/>: alerts newest first, one keyset page at a time.</summary>
internal sealed class ListJobAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<PagedResult<AlertListItem>> HandleAsync(ListAlertsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        QueryValidation.ValidateEnum(query.SeverityAtLeast, nameof(query.SeverityAtLeast));
        QueryValidation.ValidateEnum(query.DeliveryStatus, nameof(query.DeliveryStatus));
        QueryValidation.ValidatePositiveId(query.JobId, nameof(query.JobId));

        var unresolvedOnly = query.UnresolvedOnly == true ? true : (bool?)null;
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListAlertsQuery));
        var paging = Paging.Start(
            "ListJobAlerts",
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
                ("tags", tags),
            ]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListJobAlerts",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.JobId, query.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.UnresolvedOnlyFlag, unresolvedOnly));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.SeverityCode, query.SeverityAtLeast));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.DeliveryStatusCode, query.DeliveryStatus));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AcknowledgedFilter, query.AcknowledgedOnly));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, (long?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<AlertListItem>(),
            ct
        );
        return paging.Page(rows, total, static item => [item.CreatedAtUtc, item.AlertId]);
    }
}
