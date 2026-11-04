namespace Acta;

/// <summary><see cref="OutboxOperations.Source.ListQuarantinedRowsAsync"/>: one keyset read of the source's Quarantined rows.</summary>
internal sealed class ListQuarantinedOutboxRowsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListQuarantinedOutboxRowsResponse> HandleAsync(ListQuarantinedOutboxRowsQuery query, CancellationToken ct) =>
        new(
            await session.QueryListAsync(
                "ListQuarantinedOutboxRows",
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.PageSize, query.PageSize));
                    cmd.Parameters.Add(dialect.CreateParameter(OutboxSchema.Sql.AfterOutboxId, query.AfterOutboxId));
                },
                DbProjectionResolver.Resolve<ListQuarantinedOutboxItem>(),
                ct
            )
        );
}
