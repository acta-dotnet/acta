namespace Acta;

/// <summary><see cref="AlertsOperations.ResolveAlertsAsync"/>: one batch.</summary>
internal sealed class ResolveAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(ResolveAlertsCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("ResolveAlerts"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.NamespaceId, command.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SourceEventId, command.SourceEventId));
            },
            ct
        );
}
