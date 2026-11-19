using System.Globalization;

namespace Acta;

/// <summary><see cref="ILedger.ListJobsAsync"/>: resolves the parent filter, then jobs newest first, one keyset page at a time.</summary>
internal sealed class ListJobsHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async Task<ListJobsResponse> HandleAsync(ListJobsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        var jobName = QueryValidation.ValidateJobName(query.JobName, jobNamespace, nameof(query.JobName));
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));
        QueryValidation.ValidateEnum(query.Kind, nameof(query.Kind));
        QueryValidation.ValidatePositiveId((long?)query.TenantId, nameof(query.TenantId));
        var tenantName = QueryValidation.ValidateExtendedName(query.TenantName, nameof(query.TenantName));
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListJobsQuery));
        var lane = QueryValidation.ValidateExtendedName(query.Lane, nameof(query.Lane));
        var correlationKey = QueryValidation.ValidateKey(query.CorrelationKey, nameof(query.CorrelationKey), NameSyntax.DefaultMaxLength);

        // A parent that is not there has no children: answered before the cursor is read.
        long? parentJobId = null;
        if (query.Parent is { } parent && (parentJobId = (await getJobId.HandleAsync(new GetJobIdQuery(parent), ct))?.JobId) is null)
        {
            return new ListJobsResponse([], null, false, JobsQueryLimits.NormalizePageSize(query.PageSize), query.IncludeTotal ? 0 : null);
        }

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
                ("parent", parentJobId?.ToString(CultureInfo.InvariantCulture)),
                ("tenant", query.TenantId?.ToString(CultureInfo.InvariantCulture)),
                ("tenantName", tenantName),
                ("correlation", correlationKey),
                ("tags", tags),
                ("finished", query.Finished?.ToString()),
                ("kind", ((byte?)query.Kind)?.ToString(CultureInfo.InvariantCulture)),
                ("lane", lane),
            ]
        );

        var read = DbProjectionResolver.Resolve<ListJobsItem>();
        var (rows, total) = await session.QueryPageAsync(
            "ListJobs",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.JobStatusCode, query.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNameFilter, jobName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ParentJobIdFilter, parentJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.TenantId, query.TenantId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TenantNameFilter, tenantName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CorrelationKeyFilter, correlationKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.FinishedFilter, query.Finished));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobKindCode, query.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LaneFilter, lane));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCreatedAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, (long?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            read,
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.CreatedAtUtc, item.JobId]);
        return new ListJobsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
