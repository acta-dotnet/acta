namespace Acta;

/// <summary><see cref="MaintenanceOperations.PurgeExpiredLocksAsync"/>: one batch, one round trip.</summary>
internal sealed class PurgeExpiredLocksHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<PurgeExpiredLocksResponse> HandleAsync(PurgeExpiredLocksCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        var rows = await session.ExecuteAsync(
            new StoreCommand("PurgeExpiredLocks"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeCutoffUtc, command.CutoffUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeBatchSize, command.BatchSize));
            },
            static reader => new PurgeExpiredLocksResponse(reader.GetInt32(0)),
            ct
        );

        return rows.Single();
    }
}
