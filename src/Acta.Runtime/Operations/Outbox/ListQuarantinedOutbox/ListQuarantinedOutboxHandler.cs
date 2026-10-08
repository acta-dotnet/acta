namespace Acta.Runtime;

/// <summary><see cref="OutboxSource.ListQuarantinedAsync"/>: one keyset read of the source's Quarantined rows.</summary>
internal sealed class ListQuarantinedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListQuarantinedOutboxResponse> HandleAsync(ListQuarantinedOutboxQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListQuarantinedOutbox",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.PageSize, query.PageSize));
                    cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.AfterOutboxId, query.AfterOutboxId));
                },
                DbProjectionResolver.Resolve<ListOutboxQuarantinedItem>(),
                ct
            )
        );
}
