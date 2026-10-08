namespace Acta;

/// <summary>Gives back a held lock, guarded by its hold token.</summary>
internal sealed class ReleaseLockHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<ReleaseLockResponse> HandleAsync(ReleaseLockCommand command, CancellationToken ct)
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
        return new ReleaseLockResponse(rows.Count > 0);
    }
}
