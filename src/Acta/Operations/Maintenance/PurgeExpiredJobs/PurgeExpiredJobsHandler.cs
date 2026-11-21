namespace Acta;

/// <summary><see cref="MaintenanceOperations.PurgeExpiredJobsAsync"/>: one batch, one round trip.</summary>
internal sealed class PurgeExpiredJobsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<PurgeExpiredJobsResponse> HandleAsync(PurgeExpiredJobsCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        var rows = await session.ExecuteAsync(
            new StoreCommand("PurgeExpiredJobs"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeCutoffUtc, command.CutoffUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeBatchSize, command.BatchSize));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeAfterRetentionUtc, command.After?.Utc ?? DateTime.MinValue));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeAfterJobId, command.After?.JobId ?? 0));
            },
            static reader => new PurgeExpiredJobsResponse(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : new RetentionCursor(DbCellCoercion.ToUtc(reader.GetValue(1)).Ticks, reader.GetInt64(2))
            ),
            ct
        );

        return rows.Single();
    }
}
