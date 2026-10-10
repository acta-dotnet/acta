namespace Acta;

/// <summary><see cref="ISignalsOperations.WaitSignalAsync"/>: one routine call answering the wait decision.</summary>
internal sealed class WaitSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<WaitSignalResponse> HandleAsync(WaitSignalCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("WaitSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.KindCode, command.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.WaitTimeoutSeconds, command.TimeoutSeconds));
            },
            DbProjectionResolver.Resolve<WaitSignalResponse>(),
            ct
        );
}
