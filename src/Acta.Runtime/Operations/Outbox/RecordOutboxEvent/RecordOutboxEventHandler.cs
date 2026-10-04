namespace Acta.Runtime;

/// <summary>Appends the applied-command evidence event against the slot job; it is always audited.</summary>
internal sealed class RecordOutboxEventHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RecordOutboxEventCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("RecordOutboxEvent"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EventCode, command.EventCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ActorCode, ActorCode.Operator));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ActorKey, command.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ReasonMessage, command.ReasonMessage));
            },
            ct
        );
}
