using System.Globalization;

namespace Acta.Runtime;

/// <summary><see cref="ISchedules.ListAsync"/>: schedules next run first, one keyset page at a time.</summary>
internal sealed class ListJobSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<PagedResult<ScheduleListItem>> HandleAsync(ListSchedulesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        var jobName = QueryValidation.ValidateJobName(query.JobName, jobNamespace, nameof(query.JobName));
        QueryValidation.ValidateEnum(query.Origin, nameof(query.Origin));

        var liveOnly = query.LiveOnly is null or true ? true : (bool?)null;
        var tags = TagFilterJson.Normalize(query.Tags, nameof(ListSchedulesQuery));
        var paging = Paging.Start(
            "ListJobSchedules",
            "next_run_at_utc asc, id asc",
            [CursorKeyKind.Utc, CursorKeyKind.Long],
            query.Cursor,
            query.PageSize,
            [
                ("ns", jobNamespace),
                ("name", jobName),
                ("origin", ((byte?)query.Origin)?.ToString(CultureInfo.InvariantCulture)),
                ("live", liveOnly?.ToString()),
                ("tags", tags),
            ]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListJobSchedules",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNameFilter, jobName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.OriginCode, query.Origin));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LiveOnlyFlag, liveOnly));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.TagFiltersJson, tags));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNextRunAtUtc, (DateTime?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, (long?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ScheduleListItem>(),
            ct
        );

        // The list excludes rows without a next run, so the cursor key is always present.
        return paging.Page(rows, total, static item => [item.NextRunAtUtc!.Value, item.JobScheduleId]);
    }
}
