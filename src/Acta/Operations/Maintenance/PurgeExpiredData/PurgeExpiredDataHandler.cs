namespace Acta;

/// <summary>Deletes one bounded batch of a retention section.</summary>
internal sealed class PurgeExpiredDataHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<PurgeExpiredDataResponse> HandleAsync(PurgeExpiredDataCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.BatchSize);
        if (command.Section is < RetentionSection.Jobs or > RetentionSection.Lanes)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
        var rows = await session.ExecuteAsync(
            new StoreCommand("PurgeExpiredData"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeSection, (int)command.Section));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeCutoffUtc, command.CutoffUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.PurgeBatchSize, command.BatchSize));
            },
            static reader => reader.GetInt32(0),
            ct
        );

        return new PurgeExpiredDataResponse(rows.Single());
    }
}
