namespace Acta;

/// <summary>
/// <see cref="ExecutionOperations.ListChildJobsByNameAsync"/>: the parent's children under the given names, so a map's
/// replay answers the children it already started without starting them again.
/// </summary>
internal sealed class ListChildJobsByNameHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListChildJobsByNameResponse> HandleAsync(ListChildJobsByNameQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListChildJobsByName",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.ParentJobId, query.ParentJobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ChildNamesJson, DbParams.JsonStringArray(query.Names)));
                },
                DbProjectionResolver.Resolve<ListChildJobsByNameItem>(),
                ct
            )
        );
}
