namespace Acta.Runtime;

/// <summary>Keeps a held lock for another time to live; false when its hold token no longer matches.</summary>
internal sealed class ExtendLockHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<bool> HandleAsync(ExtendLockCommand command, CancellationToken ct)
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
        return rows.Count > 0;
    }
}
