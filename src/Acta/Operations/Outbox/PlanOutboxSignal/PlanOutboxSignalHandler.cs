namespace Acta;

/// <summary><see cref="OutboxOperations.PlanOutboxSignalAsync"/>: one rewrite matched by the command's stored value.</summary>
internal sealed class PlanOutboxSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<PlanOutboxSignalResponse> HandleAsync(PlanOutboxSignalCommand command, CancellationToken ct)
    {
        var row = await session.ExecuteSingleAsync(
            new StoreCommand("PlanOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Value, command.Value));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PlannedValue, command.PlannedValue));
            },
            DbProjectionResolver.Resolve<PlanOutboxSignalRow>(),
            ct
        );
        return new PlanOutboxSignalResponse(row is { Planned: > 0 });
    }
}
