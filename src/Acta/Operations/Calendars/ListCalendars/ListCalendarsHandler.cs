using System.Globalization;

namespace Acta;

/// <summary>
/// <see cref="ICalendars.ListAsync"/>: calendar families by name and then namespace, one keyset page at a time; a namespace's
/// list includes the shared families in <c>sys</c>.
/// </summary>
internal sealed class ListCalendarsHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ListCalendarsResponse> HandleAsync(ListCalendarsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace));
        QueryValidation.ValidateEnum(query.Role, nameof(query.Role));
        var fragment = QueryValidation.ValidateJobNameFragment(query.NameSearch, nameof(query.NameSearch));
        // Validation admits only name characters, so the LIKE pattern holds no wildcard but its own.
        var pattern = fragment is null ? null : "%" + fragment + "%";
        var paging = Paging.Start(
            "ListCalendars",
            "calendar_name asc, namespace asc",
            [CursorKeyKind.Text, CursorKeyKind.Text],
            query.Cursor,
            query.PageSize,
            [("ns", jobNamespace), ("role", ((byte?)query.Role)?.ToString(CultureInfo.InvariantCulture)), ("search", fragment)]
        );

        var (rows, total) = await session.QueryPageAsync(
            "ListCalendars",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceFilter, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarRoleCode, query.Role));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NameSearchFilter, pattern));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorCalendarName, (string?)paging.After?[0]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.CursorNamespaceName, (string?)paging.After?[1]));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, paging.Fetch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IncludeTotalFlag, query.IncludeTotal ? true : (bool?)null));
            },
            DbProjectionResolver.Resolve<ListCalendarsItem>(),
            ct
        );
        var (items, nextCursor, hasMore, pageSize, totalCount) = paging.Page(
            rows,
            total,
            static item => [item.CalendarName, item.JobNamespace]
        );
        return new ListCalendarsResponse(items, nextCursor, hasMore, pageSize, totalCount);
    }
}
