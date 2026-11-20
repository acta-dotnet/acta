namespace Acta;

/// <summary><see cref="MaintenanceOperations.PurgeUnusedLanesAsync"/>: one batch, one round trip.</summary>
internal sealed class PurgeUnusedLanesHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<PurgeUnusedLanesResponse> HandleAsync(PurgeUnusedLanesCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        var rows = await session.ExecuteAsync(
            new StoreCommand("PurgeUnusedLanes"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lane.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeBatchSize, command.BatchSize));
            },
            static reader => new PurgeUnusedLanesResponse(reader.GetInt32(0)),
            ct
        );

        return rows.Single();
    }
}
