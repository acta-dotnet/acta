using Acta.Relational.Commands;
using Acta.Relational.Connections;
using Acta.Relational.Schema;
using Acta.Services.Locks;

namespace Acta.Relational.Stores;

/// <summary>
/// Shared relational <c>leases</c>-backed <see cref="ILockStore"/> over <see cref="IDbSession"/>:
/// acquire is steal-on-expiry (returns the per-hold version); extend and release are version-CAS.
/// The provider mechanics (routine vs inline write) live behind the session.
/// </summary>
internal sealed class RelationalLockStore(IDbSession session, ISqlDialect dialect) : ILockStore
{
    public async Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Services", "Locks/AcquireLock"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lease.LeaseKey, key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lease.JobId, ownerJobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, (int)ttl.TotalSeconds));
            },
            reader => new LockToken(key, reader.GetInt32(0)),
            ct
        );
        return rows.Count > 0 ? rows[^1] : (LockToken?)null;
    }

    public async Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("Services", "Locks/ExtendLock"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lease.LeaseKey, token.Key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lease.Version, token.Version));
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
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lease.LeaseKey, token.Key));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Lease.Version, token.Version));
            },
            static _ => true,
            ct
        );
        return rows.Count > 0;
    }
}
