namespace Acta;

/// <summary><see cref="WorkerLifecycle.ExtendWorkerLeasesAsync"/>: one round trip.</summary>
internal sealed class ExtendWorkerLeasesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ExtendWorkerLeasesResponse> HandleAsync(ExtendWorkerLeasesCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("ExtendWorkerLeases"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.Draining, command.Draining));
                dialect.BindHeldLeases(cmd, command.Executions);
            },
            DbProjectionResolver.Resolve<ExtendWorkerLeasesRow>(),
            ct
        );

        List<ExtendWorkerLeasesLease> leases = [];
        DateTime? definitionsModifiedAtUtc = null;
        foreach (var row in rows)
        {
            if (row.JobId is { } jobId)
            {
                leases.Add(new ExtendWorkerLeasesLease(jobId, row.ExecutionNumber!.Value, row.Renewed));
            }
            else
            {
                definitionsModifiedAtUtc = row.DefinitionsModifiedAtUtc;
            }
        }

        return new ExtendWorkerLeasesResponse(leases, definitionsModifiedAtUtc);
    }
}
