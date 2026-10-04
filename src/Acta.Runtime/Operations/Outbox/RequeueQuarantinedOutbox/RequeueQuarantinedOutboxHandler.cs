namespace Acta.Runtime;

/// <summary><see cref="IOutboxSource.RequeueQuarantinedAsync"/>: one statement answering the ids it requeued.</summary>
internal sealed class RequeueQuarantinedOutboxHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<IReadOnlyList<Guid>> HandleAsync(RequeueQuarantinedOutboxCommand command, CancellationToken ct)
    {
        var affected = await session.ExecuteAsync(
            new StoreCommand("RequeueQuarantinedOutbox"),
            cmd =>
                cmd.Parameters.Add(
                    dialect.CreateParameter(OutboxSchema.Sql.OutboxIdsOptional, OutboxJson.OptionalIdArray(command.OutboxIds))
                ),
            DbProjectionResolver.Resolve<OutboxAffectedRow>(),
            ct
        );
        return [.. affected.Select(static row => row.OutboxId)];
    }
}
