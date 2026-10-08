namespace Acta.Runtime;

/// <summary>
/// <see cref="AlertLifecycle.UpdateAlertDeliveryAsync"/>: one routine call; a returned row means this
/// attempt still owned the version it observed.
/// </summary>
internal sealed class UpdateAlertDeliveryHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<UpdateAlertDeliveryResponse> HandleAsync(UpdateAlertDeliveryCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("UpdateAlertDelivery"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.Id, command.AlertId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.Version, command.ExpectedVersion));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.DeliveryStatusCode, (short)command.Status));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.RetryCount, command.RetryCount));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Alert.RetryAfterUtc, command.RetryAfterUtc));
            },
            static _ => true,
            ct
        );
        return new UpdateAlertDeliveryResponse(rows.Count != 0);
    }
}
