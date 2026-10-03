namespace Acta.Runtime;

/// <summary>Gives back a held lock; false when its hold token no longer matches.</summary>
internal sealed class ReleaseLockHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<bool> HandleAsync(ReleaseLockCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("ReleaseLock"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.LockKey, command.Token.Key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, command.Token.HoldToken));
            },
            static _ => true,
            ct
        );
        return rows.Count > 0;
    }
}
