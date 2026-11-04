namespace Acta;

/// <summary><see cref="AlertsOperations.ListDeliverableAlertsAsync"/>: one read of the alerts due for delivery.</summary>
internal sealed class ListDeliverableAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListDeliverableAlertsResponse> HandleAsync(ListDeliverableAlertsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListDeliverableAlerts",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.NamespaceId, query.NamespaceId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertBatchSize, query.BatchSize));
                },
                DbProjectionResolver.Resolve<DeliverableAlert>(),
                ct
            )
        );
}
