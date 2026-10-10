namespace Acta;

/// <summary><see cref="IOutboxSource.RescheduleAsync"/>: one token-CAS statement over the per-row records.</summary>
internal sealed class RescheduleOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RescheduleOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("RescheduleOutbox"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.RowRecords, OutboxJson.Reschedules(command.Rows)));
            },
            ct
        );
}
