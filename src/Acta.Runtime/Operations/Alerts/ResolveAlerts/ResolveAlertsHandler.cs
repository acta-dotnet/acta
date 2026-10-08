namespace Acta.Runtime;

/// <summary>
/// <see cref="AlertLifecycle.ResolveAlertsAsync"/>: one routine call answering an explicit count, so it
/// never depends on driver row-count tokens.
/// </summary>
internal sealed class ResolveAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<int> HandleAsync(ResolveAlertsCommand command, CancellationToken ct) =>
        (
            await session.ExecuteAsync(
                new StoreCommand("ResolveAlerts"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.NamespaceId, command.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.JobId, command.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SourceEventId, command.SourceEventId));
                },
                static reader => reader.GetInt32(0),
                ct
            )
        ).Single();
}
