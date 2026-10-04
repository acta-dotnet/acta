namespace Acta.Runtime;

/// <summary><see cref="OutboxSource.QuarantineAsync"/>: one token-CAS statement over the per-row records.</summary>
internal sealed class QuarantineOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(QuarantineOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("QuarantineOutbox"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.RowRecords, OutboxJson.Quarantines(command.Rows)));
            },
            ct
        );
}
