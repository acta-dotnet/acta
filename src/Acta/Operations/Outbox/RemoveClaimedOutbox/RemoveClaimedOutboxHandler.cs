namespace Acta;

/// <summary><see cref="OutboxOperations.Source.RemoveClaimedAsync"/>: one token-CAS delete of the named rows.</summary>
internal sealed class RemoveClaimedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RemoveClaimedOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("RemoveClaimedOutbox"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.OutboxIds, OutboxJson.IdArray(command.OutboxIds)));
            },
            ct
        );
}
