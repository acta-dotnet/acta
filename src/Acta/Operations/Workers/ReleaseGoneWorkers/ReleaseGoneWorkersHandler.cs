namespace Acta;

/// <summary><see cref="WorkersOperations.ReleaseGoneWorkersAsync"/>: one round trip.</summary>
internal sealed class ReleaseGoneWorkersHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(ReleaseGoneWorkersCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("ReleaseGoneWorkers"),
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.WorkerIdsJson, DbParams.JsonIdArray(command.WorkerIds))),
            ct
        );
}
