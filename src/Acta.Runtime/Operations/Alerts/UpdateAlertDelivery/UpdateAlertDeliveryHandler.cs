namespace Acta.Runtime;

/// <summary>
/// <see cref="IAlertLifecycle.UpdateAlertDeliveryAsync"/>: one routine call; a returned row means this
/// attempt still owned the version it observed.
/// </summary>
internal sealed class UpdateAlertDeliveryHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<bool> HandleAsync(UpdateAlertDeliveryCommand command, CancellationToken ct) =>
        (
            await session.ExecuteAsync(
                new StoreCommand("UpdateAlertDelivery"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.Id, command.AlertId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.Version, command.ExpectedVersion));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.DeliveryStatusCode, (short)command.Status));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.RetryCount, command.RetryCount));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobAlert.RetryAfterUtc, command.RetryAfterUtc));
                },
                static _ => true,
                ct
            )
        ).Count != 0;
}
