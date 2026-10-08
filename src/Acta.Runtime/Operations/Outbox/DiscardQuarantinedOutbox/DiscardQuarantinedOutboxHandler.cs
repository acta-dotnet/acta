namespace Acta.Runtime;

/// <summary><see cref="OutboxSource.DiscardQuarantinedAsync"/>: one statement answering the ids it discarded.</summary>
internal sealed class DiscardQuarantinedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<DiscardQuarantinedOutboxResponse> HandleAsync(DiscardQuarantinedOutboxCommand command, CancellationToken ct)
    {
        var affected = await session.ExecuteAsync(
            new StoreCommand("DiscardQuarantinedOutbox"),
            cmd =>
                cmd.Parameters.Add(
                    dialect.CreateParameter(OutboxSchema.Sql.OutboxIdsOptional, OutboxJson.OptionalIdArray(command.OutboxIds))
                ),
            DbProjectionResolver.Resolve<DiscardQuarantinedOutboxRow>(),
            ct
        );
        return new DiscardQuarantinedOutboxResponse([.. affected.Select(static row => row.OutboxId)]);
    }
}
