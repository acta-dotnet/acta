namespace Acta;

/// <summary><see cref="SchedulesOperations.ListDueSchedulesAsync"/>: the one read an admission pass makes before it plans.</summary>
internal sealed class ListDueSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListDueSchedulesResponse> HandleAsync(ListDueSchedulesQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListDueSchedules",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, query.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NowUtc, query.NowUtc));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, query.Take));
                },
                DbProjectionResolver.Resolve<DueSchedule>(),
                ct
            )
        );
}
