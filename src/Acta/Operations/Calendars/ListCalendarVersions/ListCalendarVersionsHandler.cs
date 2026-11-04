namespace Acta;

/// <summary><see cref="ICalendars.ListVersionsAsync"/>: one keyset page of the family's versions, newest first.</summary>
internal sealed class ListCalendarVersionsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListCalendarVersionsResponse> HandleAsync(ListCalendarVersionsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (jobNamespace, calendarName) = CalendarControl.Names(query.Calendar);
        var paging = Paging.Start(
            "ListCalendarVersions",
            "version desc",
            [CursorKeyKind.Int],
            query.Cursor,
            query.PageSize,
            [("namespace", jobNamespace), ("calendar", calendarName)]
        );
        var (rows, total) = await session.QueryPageAsync(
            "ListCalendarVersions",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorVersion, (int?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListCalendarVersionsItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(rows, total, static item => [item.Version]);
        return new ListCalendarVersionsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
