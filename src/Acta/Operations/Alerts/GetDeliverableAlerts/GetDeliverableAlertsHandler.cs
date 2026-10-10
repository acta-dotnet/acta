namespace Acta;

/// <summary><see cref="IAlertsOperations.GetDeliverableAlertsAsync"/>: one read of the alerts due for delivery.</summary>
internal sealed class GetDeliverableAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<GetDeliverableAlertsResponse> HandleAsync(GetDeliverableAlertsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "GetDeliverableAlerts",
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
