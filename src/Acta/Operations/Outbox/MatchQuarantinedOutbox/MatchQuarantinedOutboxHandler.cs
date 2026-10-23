namespace Acta;

/// <summary><see cref="OutboxOperations.Source.MatchQuarantinedAsync"/>: one read of the Quarantined rows a command targets.</summary>
internal sealed class MatchQuarantinedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<MatchQuarantinedOutboxResponse> HandleAsync(MatchQuarantinedOutboxQuery query, CancellationToken ct)
    {
        var rows = await session.QueryListAsync(
            "MatchQuarantinedOutbox",
            cmd =>
                cmd.Parameters.Add(
                    dialect.CreateParameter(OutboxSchema.Sql.OutboxIdsOptional, OutboxJson.OptionalIdArray(query.OutboxIds))
                ),
            DbProjectionResolver.Resolve<MatchQuarantinedOutboxRow>(),
            ct
        );
        return new MatchQuarantinedOutboxResponse([.. rows.Select(static row => row.OutboxId)]);
    }
}
