namespace Acta;

/// <summary><see cref="MaintenanceOperations.PurgeExpiredEventsAsync"/>: one batch, one round trip.</summary>
internal sealed class PurgeExpiredEventsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<PurgeExpiredEventsResponse> HandleAsync(PurgeExpiredEventsCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        var rows = await session.ExecuteAsync(
            new StoreCommand("PurgeExpiredEvents"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeCutoffUtc, command.CutoffUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeBatchSize, command.BatchSize));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeAfterCreatedUtc, command.AfterCreatedUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeAfterEventId, command.AfterEventId));
            },
            static reader => new PurgeExpiredEventsResponse(
                reader.GetInt32(0),
                DbCellCoercion.ToUtc(reader.GetValue(1)),
                reader.GetInt64(2)
            ),
            ct
        );

        return rows.Single();
    }
}
