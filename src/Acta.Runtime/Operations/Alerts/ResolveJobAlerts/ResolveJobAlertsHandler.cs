namespace Acta.Runtime;

/// <summary>
/// <see cref="AlertLifecycle.ResolveJobAlertsAsync"/>: one routine call answering an explicit count, so it
/// never depends on driver row-count tokens.
/// </summary>
internal sealed class ResolveJobAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<int> HandleAsync(ResolveJobAlertsCommand command, CancellationToken ct) =>
        (
            await session.ExecuteAsync(
                new StoreCommand("ResolveJobAlerts"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.NamespaceId, command.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.JobId, command.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SourceEventId, command.SourceEventId));
                },
                static reader => reader.GetInt32(0),
                ct
            )
        ).Single();
}
