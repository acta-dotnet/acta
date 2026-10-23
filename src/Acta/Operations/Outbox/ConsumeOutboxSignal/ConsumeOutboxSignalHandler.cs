namespace Acta;

/// <summary><see cref="OutboxOperations.ConsumeOutboxSignalAsync"/>: one delete of the command read, with its evidence event.</summary>
internal sealed class ConsumeOutboxSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ConsumeOutboxSignalResponse> HandleAsync(ConsumeOutboxSignalCommand command, CancellationToken ct)
    {
        var row = await session.ExecuteSingleAsync(
            new StoreCommand("ConsumeOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Value, command.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EventCode, command.EventCode));
                // Written as stored: ParkOutboxSignalHandler.ParkAsync validated the actor.
                cmd.AddEventFields(dialect, command.ReasonMessage, EventDetails.Write(("actor", command.Actor)));
            },
            DbProjectionResolver.Resolve<ConsumeOutboxSignalRow>(),
            ct
        );
        return new ConsumeOutboxSignalResponse(row is { Consumed: > 0 });
    }
}
