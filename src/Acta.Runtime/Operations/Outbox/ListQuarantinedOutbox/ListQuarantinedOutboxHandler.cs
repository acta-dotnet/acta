namespace Acta.Runtime;

/// <summary><see cref="OutboxSource.ListQuarantinedAsync"/>: one keyset read of the source's Quarantined rows.</summary>
internal sealed class ListQuarantinedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<IReadOnlyList<OutboxQuarantinedListItem>> HandleAsync(ListQuarantinedOutboxQuery query, CancellationToken ct) =>
        session.QueryListAsync(
            "ListQuarantinedOutbox",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.PageSize, query.PageSize));
                cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.AfterOutboxId, query.AfterOutboxId));
            },
            DbProjectionResolver.Resolve<OutboxQuarantinedListItem>(),
            ct
        );
}
