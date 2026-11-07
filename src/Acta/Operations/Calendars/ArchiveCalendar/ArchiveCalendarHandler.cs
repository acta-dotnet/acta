namespace Acta;

/// <summary>
/// <see cref="ICalendars.ArchiveAsync"/>: one batch under the namespace's exclusive schedule lock, so no registration binds
/// the family between the dependents count and the archive. An archive with no expected version goes against the current
/// version the store answers.
/// </summary>
internal sealed class ArchiveCalendarHandler(IDbSession session, ISqlDialect dialect)
{
    private const int MaxReplans = 3;

    public async ValueTask<ArchiveCalendarResponse> HandleAsync(ArchiveCalendarCommand command, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(command.Calendar);

        // The event names the version the archive retires, so the batch is sent against a version: the caller's, or else
        // the current version a refusal answers.
        var expected = command.ExpectedCurrentVersion ?? 0;
        for (var attempt = 0; ; attempt++)
        {
            var response = await ArchiveAsync(jobNamespace, calendarName, expected, command, ct);
            if (
                command.ExpectedCurrentVersion is not null
                || response is not { Action: ControlAction.Rejected, Version: { } current }
                || current == expected
                || attempt == MaxReplans
            )
            {
                return response;
            }

            expected = current;
        }
    }

    private Task<ArchiveCalendarResponse> ArchiveAsync(
        string jobNamespace,
        string calendarName,
        int expected,
        ArchiveCalendarCommand command,
        CancellationToken ct
    ) =>
        session.ExecuteOneAsync(
            new StoreCommand("ArchiveCalendar"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedCurrentVersion, expected));
                cmd.AddEventFields(
                    dialect,
                    command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    CalendarControl.Details(calendarName, expected, null, command.Actor)
                );
            },
            DbProjectionResolver.Resolve<ArchiveCalendarResponse>(),
            ct
        );
}
