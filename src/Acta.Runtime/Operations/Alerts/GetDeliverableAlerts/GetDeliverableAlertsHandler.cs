namespace Acta.Runtime;

/// <summary><see cref="AlertLifecycle.GetDeliverableAlertsAsync"/>: one read of the alerts due for delivery.</summary>
internal sealed class GetDeliverableAlertsHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<DeliverableAlert>> HandleAsync(GetDeliverableAlertsQuery query, CancellationToken ct) =>
        session.QueryListAsync(
            "GetDeliverableAlerts",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.NamespaceId, query.NamespaceId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertBatchSize, query.BatchSize));
            },
            DbProjectionResolver.Resolve<DeliverableAlert>(),
            ct
        );
}
