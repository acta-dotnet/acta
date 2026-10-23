namespace Acta;

/// <summary><see cref="ExecutionOperations.GetChildJobIdsAsync"/>: the parent's direct children, terminal ones included.</summary>
internal sealed class GetChildJobIdsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetChildJobIdsResponse> HandleAsync(GetChildJobIdsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetChildJobIds",
                cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.ParentJobId, query.ParentJobId)),
                static reader => reader.GetInt64(0),
                ct
            )
        );
}
