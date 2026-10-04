namespace Acta.Runtime;

/// <summary>
/// Version-CAS delete of an applied command row. False means a newer command superseded it while it was
/// being applied; the newer command simply applies on the next tick.
/// </summary>
internal sealed class ConsumeOutboxSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<bool> HandleAsync(ConsumeOutboxSignalCommand command, CancellationToken ct)
    {
        var row = await session.ExecuteSingleAsync(
            new StoreCommand("ConsumeOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, command.Version));
            },
            DbProjectionResolver.Resolve<OutboxSignalConsumeRow>(),
            ct
        );
        return row is { Consumed: > 0 };
    }
}
