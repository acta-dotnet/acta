using System.Data.Common;

namespace Acta;

/// <summary>
/// <see cref="CalendarsOperations.GetCalendarVersionAsync"/>: one seek by the family's key, whatever the version's status,
/// so a read of a superseded or archived version, or of a draft, still finds it.
/// </summary>
internal sealed class GetCalendarVersionHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetCalendarVersionResponse?> HandleAsync(GetCalendarVersionQuery query, CancellationToken ct)
    {
        var map = DbProjectionResolver.Resolve<GetCalendarVersionResponse>();
        return query.Transaction is { } transaction
            ? (
                await session.ExecuteInTransactionAsync(transaction, new StoreCommand("GetCalendarVersion"), Bind, map, ct)
            ).SingleOrDefault()
            : await session.QuerySingleAsync("GetCalendarVersion", Bind, map, ct);

        void Bind(DbCommand cmd)
        {
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.NamespaceId, query.NamespaceId));
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.CalendarName, query.CalendarName));
            cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.Version, query.Version));
        }
    }
}
