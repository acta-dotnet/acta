namespace Acta;

/// <summary><see cref="OutboxOperations.ConsumeOutboxSignalAsync"/>: one version-CAS delete.</summary>
internal sealed class ConsumeOutboxSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ConsumeOutboxSignalResponse> HandleAsync(ConsumeOutboxSignalCommand command, CancellationToken ct)
    {
        var row = await session.ExecuteSingleAsync(
            new StoreCommand("ConsumeOutboxSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExpectedRowVersion, command.Version));
            },
            DbProjectionResolver.Resolve<ConsumeOutboxSignalRow>(),
            ct
        );
        return new ConsumeOutboxSignalResponse(row is { Consumed: > 0 });
    }
}
