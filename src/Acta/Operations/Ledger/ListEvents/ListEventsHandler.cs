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
        QueryValidation.ValidateEnum(query.ActorCode, nameof(query.ActorCode));
        QueryValidation.ValidateEnum(query.ReasonCode, nameof(query.ReasonCode));
        QueryValidation.ValidatePositiveId(query.JobId, nameof(query.JobId));
        QueryValidation.ValidatePositiveId(query.LineageRootId, nameof(query.LineageRootId));
        if (query.IncludeTotal && query.JobId is null)
        {
            throw new InvalidQueryException("IncludeTotal on ListJobEvents requires JobId; a global event count is unbounded work.");
        }

        QueryValidation.ValidatePositiveId((long?)query.TenantId, nameof(query.TenantId));
        var tenantKey = string.IsNullOrWhiteSpace(query.TenantKey)
            ? null
            : IdentifierSyntax.NormalizeKeyLookup(query.TenantKey, nameof(query.TenantKey));
        QueryValidation.ValidatePositiveId((long?)query.WorkerId, nameof(query.WorkerId));
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListEventsQuery));

        // The cursor's operation name predates the ListEvents stem; it stays so issued cursors still decode.
        var paging = Paging.Start(
            "ListJobEvents",
            "created_at_utc desc, id desc",
            [CursorKeyKind.Utc, CursorKeyKind.Long],
            query.Cursor,
            query.PageSize,
            [
                ("job", query.JobId?.ToString(CultureInfo.InvariantCulture)),
                ("root", query.LineageRootId?.ToString(CultureInfo.InvariantCulture)),
                ("ns", jobNamespace),
                ("event", ((byte?)query.EventCode)?.ToString(CultureInfo.InvariantCulture)),
                ("def", query.DefinitionId?.ToString(CultureInfo.InvariantCulture)),
                ("tenant", query.TenantId?.ToString(CultureInfo.InvariantCulture)),
                ("tenantKey", tenantKey),
                ("worker", query.WorkerId?.ToString(CultureInfo.InvariantCulture)),
                ("actor", ((byte?)query.ActorCode)?.ToString(CultureInfo.InvariantCulture)),
                ("reason", ((byte?)query.ReasonCode)?.ToString(CultureInfo.InvariantCulture)),
                ("from", query.CreatedFromUtc?.ToString("O", CultureInfo.InvariantCulture)),
                ("to", query.CreatedToUtc?.ToString("O", CultureInfo.InvariantCulture)),
                ("tags", tags),
            ]
        );

        var read = DbProjectionResolver.Resolve<ListEventsRow>();
        var (rows, total) = await session.QueryPageAsync(
            "ListEvents",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.JobId, query.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.LineageRootId, query.LineageRootId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EventCode, query.EventCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.DefinitionId, query.DefinitionId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.TenantId, query.TenantId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TenantKeyFilter, tenantKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.WorkerId, query.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ActorCode, query.ActorCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ReasonCode, query.ReasonCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.EventCreatedFromUtc, query.CreatedFromUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.EventCreatedToUtc, query.CreatedToUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, (long?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            reader => read(reader).ToItem(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(
            rows,
            total,
            static item => [item.CreatedAtUtc, item.JobEventId]
        );
        return new ListEventsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
