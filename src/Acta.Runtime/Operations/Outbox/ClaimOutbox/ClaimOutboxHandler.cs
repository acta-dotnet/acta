namespace Acta.Runtime;

/// <summary><see cref="OutboxSource.ClaimDueAsync"/>: one statement claims the batch and returns it.</summary>
internal sealed class ClaimOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ClaimOutboxResponse> HandleAsync(ClaimOutboxCommand command, CancellationToken ct)
    {
        var claimed = await session.ExecuteAsync(
            new StoreCommand("ClaimOutbox"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimBatchSize, command.BatchSize));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
            },
            DbProjectionResolver.Resolve<ClaimOutboxRow>(),
            ct
        );

        // RETURNING and OUTPUT carry no order, so the batch is put back in staging order here.
        return new ClaimOutboxResponse([.. claimed.OrderBy(static r => r.Id)]);
    }
}
