using System.Text;

namespace Acta;

/// <summary>
/// <see cref="ICalendars.GetAsync"/>: three reads, the family's versions, the shown version's definition, and the schedules
/// bound to the family. No row is ever deleted and a version's content never changes, so a write between the reads can
/// only add a version the response does not list or move a status it lists.
/// </summary>
internal sealed class GetCalendarHandler(ResolveCalendarVersionHandler getCalendarVersion, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetCalendarResponse?> HandleAsync(GetCalendarQuery query, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(query.Calendar);
        var versions = await session.QueryListAsync(
            "GetCalendarVersions",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
            },
            DbProjectionResolver.Resolve<GetCalendarVersionsRow>(),
            ct
        );
        var current = versions.FirstOrDefault(static v => v.Status == CalendarStatusCode.Published);
        var shown = query.Version is { } requested
            ? versions.FirstOrDefault(v => v.Version == requested)
            : current ?? (versions.Count > 0 ? versions[0] : null);
        if (
            shown is null
            || await getCalendarVersion.HandleAsync(new ResolveCalendarVersionQuery(shown.NamespaceId, calendarName, shown.Version), ct)
                is not { } definition
        )
        {
            return null;
        }

        var dependents = await session.QueryListAsync(
            "GetCalendarDependents",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.NamespaceId, shown.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
            },
            DbProjectionResolver.Resolve<GetCalendarDependent>(),
            ct
        );
        return new GetCalendarResponse(
            jobNamespace,
            calendarName,
            shown.Role,
            current?.Version,
            [
                .. versions.Select(static v => new GetCalendarVersion(
                    v.Version,
                    v.Status,
                    v.ContentHash,
                    v.CreatedAtUtc,
                    v.PublishedAtUtc,
                    v.ModifiedAtUtc
                )),
            ],
            shown.Version,
            Encoding.UTF8.GetString(definition.Definition.Span),
            dependents
        );
    }
}
