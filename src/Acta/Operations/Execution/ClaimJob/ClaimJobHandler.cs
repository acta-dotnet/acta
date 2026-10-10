namespace Acta;

/// <summary>
/// <see cref="ExecutionOperations.ClaimJobAsync"/>: claims the Ready job the command names, or delegates to the claim scan
/// with a batch of one when it names none.
/// </summary>
internal sealed class ClaimJobHandler(ClaimJobsHandler claimJobs, IDbSession session, ISqlDialect dialect)
{
    public async Task<ClaimJobResponse> HandleAsync(ClaimJobCommand command, CancellationToken ct)
    {
        var options = command.Options;
        if (command.JobId is not { } jobId)
        {
            var scanned = await claimJobs.HandleAsync(new ClaimJobsCommand(options with { MaxBatch = 1 }, command.LeaseTtlSeconds), ct);
            return new ClaimJobResponse(scanned.Jobs, scanned.Horizon);
        }

        var rows = await session.ExecuteAsync(
            new StoreCommand("ClaimJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, options.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, options.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StartExecuting, options.StartExecuting));
            },
            DbProjectionResolver.Resolve<ClaimJobRow>(),
            ct
        );
        return rows.Count == 0 ? ClaimJobResponse.Empty : new ClaimJobResponse(rows.Select(ClaimedJob.From).ToList(), null);
    }
}
