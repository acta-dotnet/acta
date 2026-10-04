namespace Acta.Runtime;

/// <summary><see cref="Execution.CheckpointSlotAsync"/>: one routine call per slot action, answering exactly one row.</summary>
internal sealed class CheckpointSlotHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<CheckpointSlotRow> HandleAsync(CheckpointSlotCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("CheckpointSlot"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SlotAction, (short)command.Action));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.KindCode, command.Kind));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.ValueFormatId, command.ValueFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.Value, command.Value));
            },
            DbProjectionResolver.Resolve<CheckpointSlotRow>(),
            ct
        );
}
