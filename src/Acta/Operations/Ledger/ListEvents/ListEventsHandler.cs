using System.Globalization;

namespace Acta;

/// <summary><see cref="ILedger.ListEventsAsync"/>: events newest first, one keyset page at a time.</summary>
internal sealed class ListEventsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListEventsResponse> HandleAsync(ListEventsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        QueryValidation.ValidateEnum(query.EventCode, nameof(query.EventCode));
        QueryValidation.ValidateEnum(query.EntityKind, nameof(query.EntityKind));
        QueryValidation.ValidatePositiveId(query.JobId, nameof(query.JobId));
        QueryValidation.ValidatePositiveId(query.LineageRootJobId, nameof(query.LineageRootJobId));
        QueryValidation.ValidatePositiveId(query.EntityId, nameof(query.EntityId));
        if (query.EntityId is not null && query.EntityKind is null)
        {
            throw new InvalidQueryException("EntityId on ListEvents requires EntityKind; an id names a row only within its kind.");
        }
        if (query.IncludeTotal && query.JobId is null && query.EntityId is null)
        {
            throw new InvalidQueryException(
                "IncludeTotal on ListEvents requires JobId or EntityId; a global event count is unbounded work."
            );
        }

        QueryValidation.ValidatePositiveId((long?)query.TenantId, nameof(query.TenantId));
        var tenantName = QueryValidation.ValidateExtendedName(query.TenantName, nameof(query.TenantName));
        QueryValidation.ValidatePositiveId((long?)query.WorkerId, nameof(query.WorkerId));

        var paging = Paging.Start(
            "ListEvents",
            "created_at_utc desc, event_id desc",
            [CursorKeyKind.Utc, CursorKeyKind.Long],
            query.Cursor,
            query.PageSize,
            [
                ("job", query.JobId?.ToString(CultureInfo.InvariantCulture)),
                ("root", query.LineageRootJobId?.ToString(CultureInfo.InvariantCulture)),
                ("ns", jobNamespace),
                ("event", ((byte?)query.EventCode)?.ToString(CultureInfo.InvariantCulture)),
                ("kind", ((byte?)query.EntityKind)?.ToString(CultureInfo.InvariantCulture)),
                ("entity", query.EntityId?.ToString(CultureInfo.InvariantCulture)),
                ("def", query.DefinitionId?.ToString(CultureInfo.InvariantCulture)),
                ("tenant", query.TenantId?.ToString(CultureInfo.InvariantCulture)),
                ("tenantName", tenantName),
                ("worker", query.WorkerId?.ToString(CultureInfo.InvariantCulture)),
                ("from", query.CreatedFromUtc?.ToString("O", CultureInfo.InvariantCulture)),
                ("to", query.CreatedToUtc?.ToString("O", CultureInfo.InvariantCulture)),
            ]
        );

        var read = DbProjectionResolver.Resolve<ListEventsRow>();
        var (rows, total) = await session.QueryPageAsync(
            "ListEvents",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobIdFilter, query.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.LineageRootJobId, query.LineageRootJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EventCode, query.EventCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EntityCode, query.EntityKind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EntityId, query.EntityId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.DefinitionId, query.DefinitionId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.TenantId, query.TenantId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TenantNameFilter, tenantName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.WorkerId, query.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.EventCreatedFromUtc, query.CreatedFromUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.EventCreatedToUtc, query.CreatedToUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, (long?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            reader => read(reader).ToItem(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.CreatedAtUtc, item.EventId]);
        return new ListEventsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
