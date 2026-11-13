namespace Acta;

/// <summary><see cref="ICalendars.ListDependentsAsync"/>: one keyset page of the schedules naming the family.</summary>
internal sealed class ListCalendarDependentsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListCalendarDependentsResponse> HandleAsync(ListCalendarDependentsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (jobNamespace, calendarName) = CalendarControl.Names(query.Calendar);
        var paging = Paging.Start(
            "ListCalendarDependents",
            "job_namespace asc, job_name asc, schedule_name asc, binding asc",
            [CursorKeyKind.Text, CursorKeyKind.Text, CursorKeyKind.Text, CursorKeyKind.Int],
            query.Cursor,
            query.PageSize,
            [("namespace", jobNamespace), ("calendar", calendarName)]
        );
        var (rows, total) = await session.QueryPageAsync(
            "ListCalendarDependents",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, (string?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorJobName, (string?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorScheduleName, (string?)paging.After?[2]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorBinding, (int?)paging.After?[3]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListCalendarDependentsItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item =>
            [item.JobNamespace, item.JobName, item.ScheduleName, (int)item.Binding]
        );
        return new ListCalendarDependentsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
