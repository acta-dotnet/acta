namespace Acta;

/// <summary><see cref="MaintenanceOperations.PurgeExpiredWorkersAsync"/>: one batch, one round trip.</summary>
internal sealed class PurgeExpiredWorkersHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<PurgeExpiredWorkersResponse> HandleAsync(PurgeExpiredWorkersCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        var rows = await session.ExecuteAsync(
            new StoreCommand("PurgeExpiredWorkers"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Worker.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeCutoffUtc, command.CutoffUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeBatchSize, command.BatchSize));
            },
            static reader => new PurgeExpiredWorkersResponse(reader.GetInt32(0)),
            ct
        );

        return rows.Single();
    }
}
