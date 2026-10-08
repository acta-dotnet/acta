namespace Acta;

/// <summary>
/// <see cref="Execution.ClaimOneAsync"/>: claims the Ready job the command names, or delegates to the claim scan
/// with a batch of one when it names none.
/// </summary>
internal sealed class ClaimOneHandler(ClaimBatchHandler claimBatch, IDbSession session, ISqlDialect dialect)
{
    public async Task<ClaimOneResponse> HandleAsync(ClaimOneCommand command, CancellationToken ct)
    {
        var request = command.Request;
        if (command.JobId is not { } jobId)
        {
            var scanned = await claimBatch.HandleAsync(new ClaimBatchCommand(request with { MaxBatch = 1 }, command.LeaseTtlSeconds), ct);
            return new ClaimOneResponse(scanned.Jobs, scanned.Horizon);
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
            DbProjectionResolver.Resolve<ClaimOneRow>(),
            ct
        );
        return rows.Count == 0 ? ClaimOneResponse.Empty : new ClaimOneResponse(rows.Select(ClaimedJob.From).ToList(), null);
    }
}
