using System.Globalization;

namespace Acta;

/// <summary>
/// <see cref="ICalendars.SaveDraftAsync"/>: compiles the definition, and saves one that compiles as the family's next
/// version in one batch under the namespace's exclusive schedule lock, which orders it with every other catalog write and
/// with registration. A definition that does not compile writes nothing.
/// </summary>
internal sealed class SaveCalendarDraftHandler(IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<SaveCalendarDraftResponse> HandleAsync(SaveCalendarDraftCommand command, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(command.Calendar);
        var role = Known(command.Role);
        CompiledCalendar compiled;
        try
        {
            compiled = CalendarDefinition.Compile(role, command.Definition);
        }
        catch (CalendarDefinitionException ex)
        {
            return new SaveCalendarDraftResponse(ControlAction.Rejected, null, null, ex.Problems);
        }

        var row = await session.ExecuteOneAsync(
            new StoreCommand("SaveCalendarDraft"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarRoleCode, command.Role));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedLatestVersion, command.ExpectedLatestVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.DefinitionFormatId, JobPayloadFormat.Json.Id));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.Definition, compiled.CanonicalJson.ToArray()));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.ContentHash, compiled.ContentHash));
                cmd.AddEventFields(
                    dialect,
                    command.ReasonMessage.Truncate(ActaTextLimits.EventMessage),
                    CalendarControl.Details(calendarName, command.ExpectedLatestVersion + 1, command.Role, command.Actor)
                );
            },
            DbProjectionResolver.Resolve<SaveCalendarDraftRow>(),
            ct
        );
        return new SaveCalendarDraftResponse(row.Action, row.Version, row.ContentHash, Problems(row, command, calendarName));
    }

    private static CalendarRoleCode Known(CalendarRoleCode role) =>
        role is CalendarRoleCode.Occurrence or CalendarRoleCode.Availability
            ? role
            : throw new ArgumentOutOfRangeException(nameof(role), role, "A calendar's role is Occurrence or Availability.");

    private static IReadOnlyList<string> Problems(SaveCalendarDraftRow row, SaveCalendarDraftCommand command, string calendarName) =>
        row.Action switch
        {
            ControlAction.VersionConflict =>
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The family's latest version is {row.Version}, not {command.ExpectedLatestVersion}."
                ),
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
