using System.Text.Json;

namespace Acta;

/// <summary>
/// <see cref="ICalendars.GetAsync"/>: the family's head, then the shown version's definition. No row is ever deleted and a
/// version's content never changes, so a write between the reads can only add a version or move a status.
/// </summary>
internal sealed class GetCalendarHandler(GetCalendarVersionHandler getCalendarVersion, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<GetCalendarResponse?> HandleAsync(GetCalendarQuery query, CancellationToken ct)
    {
        var (jobNamespace, calendarName) = CalendarControl.Names(query.Calendar);
        var head = await session.QuerySingleAsync(
            "GetCalendar",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NamespaceName, jobNamespace));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, calendarName));
            },
            DbProjectionResolver.Resolve<GetCalendarRow>(),
            ct
        );
        if (head is null)
        {
            return null;
        }

        // Versions number from 1 with no gaps and none is deleted, so one past the latest is not this family's.
        var shown = query.Version ?? head.CurrentVersion ?? head.LatestVersion;
        if (
            shown < 1
            || shown > head.LatestVersion
            || await getCalendarVersion.HandleAsync(new GetCalendarVersionQuery(head.NamespaceId, calendarName, shown), ct)
                is not { } version
        )
        {
            return null;
        }

        // A draft stores its definition in the compiler's compact form, which a compact writer with the default encoder
        // writes back unchanged, so the HTTP read carries the stored bytes.
        return new GetCalendarResponse(
            jobNamespace,
            calendarName,
            version.Role,
            head.CurrentVersion,
            head.LatestVersion,
            shown,
            version.Status,
            JsonElement.Parse(version.Definition.Span)
        );
    }
}
