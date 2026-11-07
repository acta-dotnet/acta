using System.Data.Common;

namespace Acta;

/// <summary><see cref="SchedulesOperations.ListScheduleStatesAsync(int, CancellationToken)"/>: one read.</summary>
internal sealed class ListScheduleStatesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListScheduleStatesResponse> HandleAsync(ListScheduleStatesQuery query, CancellationToken ct)
    {
        var map = DbProjectionResolver.Resolve<StoredScheduleState>();
        return new(
            query.Transaction is { } transaction
                ? await session.ExecuteInTransactionAsync(transaction, new StoreCommand("ListScheduleStates"), Bind, map, ct)
                : await session.QueryListAsync("ListScheduleStates", Bind, map, ct)
        );

        void Bind(DbCommand cmd) => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.NamespaceId, query.NamespaceId));
    }
}
