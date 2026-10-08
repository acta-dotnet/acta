namespace Acta.Runtime;

/// <summary>Keeps a held lock for another time to live, guarded by its hold token.</summary>
internal sealed class ExtendLockHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ExtendLockResponse> HandleAsync(ExtendLockCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("ExtendLock"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.LockKey, command.Token.Key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, command.Token.HoldToken));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)command.Ttl.TotalSeconds));
            },
            static _ => true,
            ct
        );
        return new ExtendLockResponse(rows.Count > 0);
    }
}
