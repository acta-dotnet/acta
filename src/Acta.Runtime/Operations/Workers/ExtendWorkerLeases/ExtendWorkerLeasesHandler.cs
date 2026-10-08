namespace Acta.Runtime;

/// <summary><see cref="WorkerLifecycle.ExtendWorkerLeasesAsync"/>: one round trip.</summary>
internal sealed class ExtendWorkerLeasesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ExtendWorkerLeasesResponse> HandleAsync(ExtendWorkerLeasesCommand command, CancellationToken ct) =>
        new(
            await session.ExecuteAsync(
                new StoreCommand("ExtendWorkerLeases"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.Draining, command.Draining));
                },
                DbProjectionResolver.Resolve<ExtendWorkerLeasesRow>(),
                ct
            )
        );
}
