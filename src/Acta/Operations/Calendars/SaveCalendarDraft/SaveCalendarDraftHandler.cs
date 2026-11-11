using System.Globalization;

namespace Acta;

/// <summary>
/// <see cref="ICalendars.SaveDraftAsync"/>: compiles the definition, which throws when it does not compile, and saves it as
/// the family's next version in one batch under the namespace's exclusive schedule lock, which orders it with every other
/// catalog write and with registration. A save with no expected version goes against the latest version the store answers.
/// </summary>
internal sealed class SaveCalendarDraftHandler(IDbSession session, ISqlDialect dialect)
{
    private const int MaxReplans = 3;

    public async ValueTask<SaveCalendarDraftResponse> HandleAsync(SaveCalendarDraftCommand command, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(command.Calendar);
        var compiled = CalendarDefinition.Compile(Known(command.Role), command.Definition);

        // The event names the version the save writes, so the batch is sent against a version: the caller's, or else a new
        // family's 0 and then the latest version each conflict answers.
        var expected = command.ExpectedLatestVersion ?? 0;
        for (var attempt = 0; ; attempt++)
        {
            var row = await SaveAsync(command, jobNamespace, calendarName, compiled, expected, ct);
            if (row.Action != ControlAction.VersionConflict || command.ExpectedLatestVersion is not null || attempt == MaxReplans)
            {
                return new SaveCalendarDraftResponse(
                    row.Action,
                    row.Version,
                    row.ContentHash,
                    Problems(row, command, expected, calendarName)
                );
            }

            expected = row.Version!.Value;
        }
    }

    private Task<SaveCalendarDraftRow> SaveAsync(
        SaveCalendarDraftCommand command,
        string jobNamespace,
        string calendarName,
        CompiledCalendar compiled,
        long expected,
        CancellationToken ct
    ) =>
        session.ExecuteOneAsync(
            new StoreCommand("SaveCalendarDraft"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarRoleCode, command.Role));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedLatestVersion, DbParams.Int32Version(expected)));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.DefinitionFormatId, JobPayloadFormat.Json.Id));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.Definition, compiled.CanonicalJson.ToArray()));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.ContentHash, compiled.ContentHash));
                cmd.AddEventFields(
                    dialect,
                    command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    JobControlActor.Operator(command.Actor),
                    CalendarControl.Details(calendarName, expected + 1, command.Role)
                );
            },
            DbProjectionResolver.Resolve<SaveCalendarDraftRow>(),
            ct
        );

    private static CalendarRoleCode Known(CalendarRoleCode role) =>
        role is CalendarRoleCode.Occurrence or CalendarRoleCode.Availability
            ? role
            : throw new ArgumentOutOfRangeException(nameof(role), role, "A calendar's role is Occurrence or Availability.");

    private static IReadOnlyList<string> Problems(
        SaveCalendarDraftRow row,
        SaveCalendarDraftCommand command,
        long expected,
        string calendarName
    ) =>
        row.Action switch
        {
            ControlAction.VersionConflict =>
            [
                string.Create(CultureInfo.InvariantCulture, $"The family's latest version is {row.Version}, not {expected}."),
            ],
            ControlAction.Rejected when row.FamilyRole is { } role && role != command.Role => [$"The family's role is {role.Code}."],
            ControlAction.Rejected when row.ClashNamespace == NameSyntax.ReservedSystemName =>
            [
                $"'{calendarName}' is a shared calendar in sys; a namespace's calendar cannot take a shared calendar's name.",
            ],
            ControlAction.Rejected when row.ClashNamespace is { } other =>
            [
                $"'{calendarName}' is a calendar of the namespace '{other}'; a shared calendar cannot take a name a namespace holds.",
            ],
            _ => [],
        };
}
