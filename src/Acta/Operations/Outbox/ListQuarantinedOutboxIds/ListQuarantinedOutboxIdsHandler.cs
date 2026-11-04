namespace Acta;

/// <summary><see cref="OutboxOperations.Source.ListQuarantinedIdsAsync"/>: one read of the Quarantined rows a command targets.</summary>
internal sealed class ListQuarantinedOutboxIdsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ListQuarantinedOutboxIdsResponse> HandleAsync(ListQuarantinedOutboxIdsQuery query, CancellationToken ct)
    {
        var rows = await session.QueryListAsync(
            "ListQuarantinedOutboxIds",
            cmd =>
                cmd.Parameters.Add(
                    dialect.CreateParameter(OutboxSchema.Sql.OutboxIdsOptional, OutboxJson.OptionalIdArray(query.OutboxIds))
                ),
            DbProjectionResolver.Resolve<ListQuarantinedOutboxIdsRow>(),
            ct
        );
        return new ListQuarantinedOutboxIdsResponse([.. rows.Select(static row => row.OutboxId)]);
    }
}
