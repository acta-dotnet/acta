using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Runtime.Services.Locks;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <c>locks</c>-backed <see cref="ILockStore"/> over <see cref="IDbSession"/>:
/// acquire is steal-on-expiry stamping a caller-minted hold token; extend and release are
/// token-CAS. Minting the token in code keeps the SQL free of per-dialect uuid generation and
/// makes acquire success a plain row-count. The provider mechanics (routine vs inline write) live
/// behind the session. The slot acquire is one round trip whatever the limit: the provider SQL
/// enumerates the slots and inserts the first free one.
/// </summary>
internal sealed class RelationalLockStore(IDbSession session, ISqlDialect dialect) : ILockStore
{
    public async Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct)
    {
        var holdToken = Guid.NewGuid();
        var rows = await session.ExecuteAsync(
            new StoreCommand("Services", "Locks/AcquireLock"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.LockKey, key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.JobId, ownerJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)ttl.TotalSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, holdToken));
            },
            static _ => true,
            ct
        );
        return rows.Count > 0 ? new LockToken(key, holdToken) : null;
    }

    public async Task<LockToken?> TryAcquireSlotAsync(string keyPrefix, int limit, TimeSpan ttl, long ownerJobId, CancellationToken ct)
    {
        var holdToken = Guid.NewGuid();
        var rows = await session.ExecuteAsync(
            new StoreCommand("Services", "Locks/AcquireSlot"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LockKeyPrefix, keyPrefix));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SlotCount, limit));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.JobId, ownerJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)ttl.TotalSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, holdToken));
            },
            // The routine picks which slot to take, so the taken key comes back rather than being
            // recomposed here; extend and release then work on it like any other lock key.
            static reader =>
                reader.GetString(0),
            ct
        );
        return rows.Count > 0 ? new LockToken(rows[0], holdToken) : null;
    }

    public async Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Services", "Locks/ExtendLock"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.LockKey, token.Key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, token.HoldToken));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)ttl.TotalSeconds));
            },
            static _ => true,
            ct
        );
        return rows.Count > 0;
    }

    public async Task<bool> ReleaseAsync(LockToken token, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Services", "Locks/ReleaseLock"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.LockKey, token.Key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lock.HoldToken, token.HoldToken));
            },
            static _ => true,
            ct
        );
        return rows.Count > 0;
    }
}
