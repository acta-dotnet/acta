using System.Globalization;

namespace Acta;

/// <summary><see cref="ILedger.ListJobsAsync"/>: jobs newest first, one keyset page at a time.</summary>
internal sealed class ListJobsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListJobsResponse> HandleAsync(ListJobsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        var jobName = QueryValidation.ValidateJobName(query.JobName, jobNamespace, nameof(query.JobName));
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));
        QueryValidation.ValidatePositiveId(query.ParentJobId, nameof(query.ParentJobId));
        QueryValidation.ValidatePositiveId((long?)query.TenantId, nameof(query.TenantId));
        var tenantKey = string.IsNullOrWhiteSpace(query.TenantKey)
            ? null
            : IdentifierSyntax.NormalizeKeyLookup(query.TenantKey, nameof(query.TenantKey));
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListJobsQuery));
        // Tri-state flags: only true restricts, so false folds to null before hashing and binding.
        var terminalOnly = query.TerminalOnly == true ? (bool?)true : null;
        var recurringOnly = query.RecurringOnly == true ? (bool?)true : null;
        var lane = string.IsNullOrWhiteSpace(query.Lane) ? null : IdentifierSyntax.NormalizeKeyLookup(query.Lane, nameof(query.Lane));

        var paging = Paging.Start(
            "ListJobs",
            "created_at_utc desc, id desc",
            [CursorKeyKind.Utc, CursorKeyKind.Long],
            query.Cursor,
            query.PageSize,
            [
                ("ns", jobNamespace),
                ("status", ((byte?)query.Status)?.ToString(CultureInfo.InvariantCulture)),
                ("name", jobName),
                ("parent", query.ParentJobId?.ToString(CultureInfo.InvariantCulture)),
                ("tenant", query.TenantId?.ToString(CultureInfo.InvariantCulture)),
                ("tenantKey", tenantKey),
                ("correlation", query.CorrelationKey),
                ("tags", tags),
                ("terminal", terminalOnly is null ? null : "1"),
                ("recurring", recurringOnly is null ? null : "1"),
                ("lane", lane),
            ]
        );

        var read = DbProjectionResolver.Resolve<ListJobsItem>();
        var (rows, total) = await session.QueryPageAsync(
            "ListJobs",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Runtime.StatusCode, query.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNameFilter, jobName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ParentIdFilter, query.ParentJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.TenantId, query.TenantId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TenantKeyFilter, tenantKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CorrelationKeyFilter, query.CorrelationKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TerminalOnlyFlag, terminalOnly));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.RecurringOnlyFlag, recurringOnly));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LaneFilter, lane));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, (long?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            reader =>
            {
                var item = read(reader);
                return item.DeduplicationKey is null
                    ? item
                    : item with
                    {
                        DeduplicationKey = PersistedDeduplicationKey.Normalize(item.DeduplicationKey),
                    };
            },
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.CreatedAtUtc, item.JobId]);
        return new ListJobsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
