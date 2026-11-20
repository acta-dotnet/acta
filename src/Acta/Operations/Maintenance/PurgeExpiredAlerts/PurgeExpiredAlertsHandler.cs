namespace Acta;

/// <summary><see cref="MaintenanceOperations.PurgeExpiredAlertsAsync"/>: one batch, one round trip.</summary>
internal sealed class PurgeExpiredAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<PurgeExpiredAlertsResponse> HandleAsync(PurgeExpiredAlertsCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        var rows = await session.ExecuteAsync(
            new StoreCommand("PurgeExpiredAlerts"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeCutoffUtc, command.CutoffUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeBatchSize, command.BatchSize));
            },
            static reader => new PurgeExpiredAlertsResponse(reader.GetInt32(0), reader.GetInt32(1)),
            ct
        );

        return rows.Single();
    }
}
