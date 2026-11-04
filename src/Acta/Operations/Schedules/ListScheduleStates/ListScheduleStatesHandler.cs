namespace Acta;

/// <summary><see cref="SchedulesOperations.ListScheduleStatesAsync"/>: one read.</summary>
internal sealed class ListScheduleStatesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListScheduleStatesResponse> HandleAsync(ListScheduleStatesQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListScheduleStates",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Schedule.NamespaceId, query.NamespaceId)),
                DbProjectionResolver.Resolve<StoredScheduleState>(),
                ct
            )
        );
}
