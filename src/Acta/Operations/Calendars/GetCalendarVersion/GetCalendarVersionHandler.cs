namespace Acta;

/// <summary>
/// <see cref="CalendarsOperations.GetCalendarVersionAsync"/>: one seek by the family's key, whatever the version's status,
/// so a read of a superseded or archived version, or of a draft, still finds it.
/// </summary>
internal sealed class GetCalendarVersionHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<GetCalendarVersionResponse?> HandleAsync(GetCalendarVersionQuery query, CancellationToken ct) =>
        session.QuerySingleAsync(
            "GetCalendarVersion",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.NamespaceId, query.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, query.CalendarName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.Version, query.Version));
            },
            DbProjectionResolver.Resolve<GetCalendarVersionResponse>(),
            ct
        );
}
