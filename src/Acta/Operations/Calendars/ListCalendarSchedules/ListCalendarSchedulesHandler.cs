namespace Acta;

/// <summary>Reads the schedules a publication retimes, in the admitter's row shape so the plan evaluates them the same way.</summary>
internal sealed class ListCalendarSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListCalendarSchedulesResponse> HandleAsync(ListCalendarSchedulesQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListCalendarSchedules",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, query.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, query.CalendarName));
                },
                DbProjectionResolver.Resolve<DueSchedule>(),
                ct
            )
        );
}
