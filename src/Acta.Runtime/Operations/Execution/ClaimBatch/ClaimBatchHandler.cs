namespace Acta.Runtime;

/// <summary><see cref="Execution.ClaimBatchAsync"/>: one routine call claims up to the batch; a batch of none claims nothing.</summary>
internal sealed class ClaimBatchHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ClaimResponse> HandleAsync(ClaimBatchCommand command, CancellationToken ct)
    {
        var request = command.Request;
        if (request.MaxBatch <= 0)
        {
            return ClaimResponse.Empty;
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("ClaimBatch"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, request.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, request.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ClaimLimit, request.MaxBatch));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StartExecuting, request.StartExecuting));
                dialect.BindExcludedDefinitionIds(cmd, request.ExcludedDefinitionIds);
            },
            DbProjectionResolver.Resolve<ClaimBatchRow>(),
            ct
        );
        return ClaimResponseMapper.Map(rows);
    }
}
