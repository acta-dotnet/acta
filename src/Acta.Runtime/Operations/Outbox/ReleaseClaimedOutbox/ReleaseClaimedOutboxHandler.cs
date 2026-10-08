namespace Acta.Runtime;

/// <summary><see cref="OutboxSource.ReleaseClaimedAsync"/>: one token-CAS release of the named rows.</summary>
internal sealed class ReleaseClaimedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(ReleaseClaimedOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("ReleaseClaimedOutbox"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.OutboxIds, OutboxJson.IdArray(command.OutboxIds)));
            },
            ct
        );
}
