namespace Acta;

/// <summary><see cref="OutboxOperations.RecordOutboxEventAsync"/>: one insert.</summary>
internal sealed class RecordOutboxEventHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RecordOutboxEventCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("RecordOutboxEvent"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EventCode, command.EventCode));
                // Written as stored: ParkOutboxSignalHandler.ParkAsync validated the actor.
                cmd.AddEventFields(dialect, command.ReasonMessage, EventDetails.Write(("actor", command.Actor)));
            },
            ct
        );
}
