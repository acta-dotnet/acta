namespace Acta;

/// <summary><see cref="WorkersOperations.StopWorkerAsync"/>: one round trip, no result.</summary>
internal sealed class StopWorkerHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(StopWorkerCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("StopWorker"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.WorkerId, command.WorkerId));
            },
            ct
        );
}
