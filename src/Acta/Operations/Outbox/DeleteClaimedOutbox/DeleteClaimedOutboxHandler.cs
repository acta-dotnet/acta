namespace Acta;

/// <summary><see cref="IOutboxSource.DeleteClaimedAsync"/>: one token-CAS delete of the named rows.</summary>
internal sealed class DeleteClaimedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(DeleteClaimedOutboxCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("DeleteClaimedOutbox"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.ClaimToken, command.ClaimToken));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.OutboxIds, OutboxJson.IdArray(command.OutboxIds)));
            },
            ct
        );
}
