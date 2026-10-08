namespace Acta.Runtime;

/// <summary>
/// Takes a lock, stealing it when the current hold has expired, and stamps a hold token minted here, so
/// the SQL needs no per-dialect uuid generation and success is a plain row count.
/// </summary>
internal sealed class AcquireLockHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<AcquireLockResponse> HandleAsync(AcquireLockCommand command, CancellationToken ct)
    {
        var holdToken = Guid.NewGuid();
        var rows = await session.ExecuteAsync(
            new StoreCommand("AcquireLock"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.LockKey, command.Key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.JobId, command.OwnerJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)command.Ttl.TotalSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, holdToken));
            },
            static _ => true,
            ct
        );
        return new AcquireLockResponse(rows.Count > 0 ? new LockToken(command.Key, holdToken) : null);
    }
}
