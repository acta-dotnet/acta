namespace Acta;

/// <summary>
/// <see cref="ICalendars.ArchiveAsync"/>: one batch under the namespace's exclusive schedule lock, so no registration binds
/// the family between the dependents count and the archive.
/// </summary>
internal sealed class ArchiveCalendarHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<ArchiveCalendarResponse> HandleAsync(ArchiveCalendarCommand command, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(command.Calendar);
        return await session.ExecuteOneAsync(
            new StoreCommand("ArchiveCalendar"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedCurrentVersion, command.ExpectedCurrentVersion));
                cmd.AddEventFields(
                    dialect,
                    command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    CalendarControl.Details(calendarName, command.ExpectedCurrentVersion, null, command.Actor)
                );
            },
            DbProjectionResolver.Resolve<ArchiveCalendarResponse>(),
            ct
        );
    }
}
