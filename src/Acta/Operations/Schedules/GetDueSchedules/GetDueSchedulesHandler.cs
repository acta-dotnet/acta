namespace Acta;

/// <summary><see cref="SchedulesOperations.GetDueSchedulesAsync"/>: the one read an admission pass makes before it plans.</summary>
internal sealed class GetDueSchedulesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetDueSchedulesResponse> HandleAsync(GetDueSchedulesQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetDueSchedules",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Namespace.NamespaceId, query.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.NowUtc, query.NowUtc));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PageTake, query.Take));
                },
                DbProjectionResolver.Resolve<GetDueSchedulesRow>(),
                ct
            )
        );
}
