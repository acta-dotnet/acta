namespace Acta.Runtime;

/// <summary>
/// <see cref="IExecution.ClaimOneAsync"/>: claims the Ready job the command names, or delegates to the claim scan
/// with a batch of one when it names none.
/// </summary>
internal sealed class ClaimOneHandler(ClaimBatchHandler claimBatch, IDbSession session, ISqlDialect dialect)
{
    public async Task<ClaimResult> HandleAsync(ClaimOneCommand command, CancellationToken ct)
    {
        var request = command.Request;
        if (command.JobId is not { } jobId)
        {
            return await claimBatch.HandleAsync(new ClaimBatchCommand(request with { MaxBatch = 1 }, command.LeaseTtlSeconds), ct);
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("ClaimOne"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, request.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, request.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StartExecuting, request.StartExecuting));
            },
            DbProjectionResolver.Resolve<ClaimReadyRow>(),
            ct
        );
        return rows.Count == 0 ? ClaimResult.Empty : new ClaimResult(rows.Select(ClaimResultMapper.ToClaimedJob).ToList(), null);
    }
}
