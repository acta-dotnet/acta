namespace Acta.Runtime;

/// <summary><see cref="OutboxSource.ListQuarantinedAsync"/>: one keyset read of the source's Quarantined rows.</summary>
internal sealed class ListQuarantinedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<OutboxQuarantinedListItem>> HandleAsync(ListQuarantinedOutboxCommand command, CancellationToken ct) =>
        session.QueryListAsync(
            "ListQuarantinedOutbox",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.PageSize, command.PageSize));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.AfterOutboxId, command.AfterOutboxId));
            },
            DbProjectionResolver.Resolve<OutboxQuarantinedListItem>(),
            ct
        );
}
