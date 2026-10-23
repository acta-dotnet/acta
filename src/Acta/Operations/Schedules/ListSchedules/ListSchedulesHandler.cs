using System.Globalization;

namespace Acta;

/// <summary>
/// <see cref="ISchedules.ListAsync"/>: the live list pages by next occurrence; the full list, which includes schedules
/// with no next occurrence, pages by slot job and schedule name.
/// </summary>
internal sealed class ListSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListSchedulesResponse> HandleAsync(ListSchedulesQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        var jobName = QueryValidation.ValidateJobName(query.JobName, jobNamespace, nameof(query.JobName));
        QueryValidation.ValidateEnum(query.Origin, nameof(query.Origin));

        var liveOnly = query.LiveOnly is null or true ? true : (bool?)null;
        ReadOnlySpan<(string, string?)> filters =
        [
            ("ns", jobNamespace),
            ("name", jobName),
            ("origin", ((byte?)query.Origin)?.ToString(CultureInfo.InvariantCulture)),
            ("live", liveOnly?.ToString()),
        ];
        var paging = liveOnly is true
            ? Paging.Start(
                "ListSchedules",
                "next_occurrence_at_utc asc, job_id asc, schedule_name asc",
                [CursorKeyKind.Utc, CursorKeyKind.Long, CursorKeyKind.Text],
                query.Cursor,
                query.PageSize,
                filters
            )
            : Paging.Start(
                "ListSchedules",
                "job_id asc, schedule_name asc",
                [CursorKeyKind.Long, CursorKeyKind.Text],
                query.Cursor,
                query.PageSize,
                filters
            );
        var after = paging.After;
        var keyOffset = liveOnly is true ? 1 : 0;

        var (rows, total) = await session.QueryPageAsync(
            "ListSchedules",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.JobNameFilter, jobName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.ScheduleOriginCode, query.Origin));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LiveOnlyFlag, liveOnly));
                cmd.Parameters.Add(
                    dialect.CreateParameter(ActaSchema.Sql.CursorNextOccurrenceAtUtc, liveOnly is true ? (DateTime?)after?[0] : null)
                );
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorId, (long?)after?[keyOffset]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorScheduleName, (string?)after?[keyOffset + 1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListSchedulesItem>(),
            ct
        );

        // The live list holds only rows with a next occurrence, so its first cursor key is always present.
        var (items, nextCursor, hasMore, pageSize, totalCount) = liveOnly is true
            ? paging.Page(rows, total, static item => [item.NextOccurrenceAtUtc!.Value, item.JobId, item.ScheduleName])
            : paging.Page(rows, total, static item => [item.JobId, item.ScheduleName]);
        return new ListSchedulesResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
