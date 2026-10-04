namespace Acta.Runtime;

/// <summary>Appends the applied-command evidence event against the slot job; it is always audited.</summary>
internal sealed class RecordOutboxEventHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RecordOutboxEventCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("RecordOutboxEvent"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobCheckpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.EventCode, command.EventCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorCode, ActorCode.Operator));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ActorKey, command.ActorKey));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.ReasonMessage));
            },
            ct
        );
}
