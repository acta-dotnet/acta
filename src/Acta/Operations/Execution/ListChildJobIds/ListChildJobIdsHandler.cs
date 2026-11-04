namespace Acta;

/// <summary><see cref="ExecutionOperations.ListChildJobIdsAsync"/>: the parent's direct children, terminal ones included.</summary>
internal sealed class ListChildJobIdsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListChildJobIdsResponse> HandleAsync(ListChildJobIdsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListChildJobIds",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.ParentJobId, query.ParentJobId)),
                static reader => reader.GetInt64(0),
                ct
            )
        );
}
