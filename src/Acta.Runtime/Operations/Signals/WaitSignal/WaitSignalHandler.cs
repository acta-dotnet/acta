namespace Acta.Runtime;

/// <summary><see cref="ISignals.WaitSignalAsync"/>: one routine call answering the wait decision.</summary>
internal sealed class WaitSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<SignalWaitDecision> HandleAsync(WaitSignalCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("WaitSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.KindCode, command.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.WaitTimeoutSeconds, command.TimeoutSeconds));
            },
            DbProjectionResolver.Resolve<SignalWaitDecision>(),
            ct
        );
}
