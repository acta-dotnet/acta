using System.Data.Common;

namespace Acta;

/// <summary><see cref="CalendarsOperations.ListCurrentCalendarsAsync"/>: one read of the namespace's Published rows.</summary>
internal sealed class ListCurrentCalendarsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListCurrentCalendarsResponse> HandleAsync(ListCurrentCalendarsQuery query, CancellationToken ct)
    {
        var map = DbProjectionResolver.Resolve<ListCurrentCalendarsItem>();
        return new(
            query.Transaction is { } transaction
                ? await session.ExecuteInTransactionAsync(transaction, new StoreCommand("ListCurrentCalendars"), Bind, map, ct)
                : await session.QueryListAsync("ListCurrentCalendars", Bind, map, ct)
        );

        void Bind(DbCommand cmd) => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Calendar.NamespaceId, query.NamespaceId));
    }
}
