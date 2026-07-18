using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// One row per held named lock in the <c>leases</c> table, discriminated by <see cref="Kind"/>:
/// <c>Lock</c> rows back both the handler-facing <c>JobContext.RunWithLock</c> and the
/// <c>exclusive_key</c> execution mutex the runner takes after claim; execution ownership/TTL
/// lives on the <c>runtimes</c> row. Lifecycle is <see cref="ExpiresAtUtc"/> alone (no status column):
/// held while ahead of now. Acquire is a steal-on-expiry upsert (a contended acquire succeeds only
/// on an expired row, rewriting the holder and bumping <see cref="Version"/>); release DELETEs the
/// row; abandoned lock rows are swept by the <c>sys.retention</c> reap. <see cref="Version"/> is a
/// per-hold CAS token: acquire returns it, release and steal guard on it
/// (<c>WHERE lease_key = @k AND version = @mine</c>) so a holder stolen from never frees its
/// successor's lock. <see cref="LeaseKey"/> is an opaque discriminator-segmented composite
/// (<c>{namespace_id}.lock.{key}</c>, <c>global.lock.{key}</c>, <c>{namespace_id}.excl.{key}</c>)
/// so the lock spaces never collide
/// on identical user text; keying on namespace id keeps the key compact and stable across renames.
/// </summary>
[DbTable("leases")]
[DbPrimaryKey(Name = "pk_leases", Columns = ["lease_key"])]
[DbIndex(Name = "ix_leases_reclaim_expired", Columns = ["kind_code", "expires_at_utc"], Usage = "lock_reclaim")]
internal sealed class Lease : IEntity
{
    /// <summary>
    /// Opaque composite lease identity and primary key. Holds a discriminator-segmented string
    /// (<c>{ns}.lock.{key}</c> / <c>global.lock.{key}</c> / <c>{ns}.excl.{key}</c>); never parsed for
    /// safety, since keys are compared as whole strings.
    /// </summary>
    [DbColumn("lease_key", DbKind.AsciiString, Size = 256)]
    public string LeaseKey { get; init; } = default!;

    /// <summary>
    /// Which lock primitive owns this row (<c>Lock</c> today; the discriminator keeps the kind
    /// space open without a migration). The retention reap sweeps expired rows by kind.
    /// </summary>
    [DbColumn("kind_code")]
    public LeaseKindCode Kind { get; init; } = LeaseKindCode.Lock;

    /// <summary>
    /// Job that currently holds (or last held) the lease. No FK; written by the acquire routine for
    /// observability ("which job holds this lock"). Release is version-CAS on the PK, not on this column.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; set; }

    /// <summary>
    /// Instant the current hold expires. Held while ahead of now; free once at or before now (stealable
    /// in place via steal-on-expiry). Release deletes the row rather than expiring it.
    /// </summary>
    [DbColumn("expires_at_utc", DbKind.UtcInstant)]
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Per-hold CAS token. Set to 1 on a fresh acquire and bumped on each steal-on-expiry; the acquire
    /// routine returns it, and release/steal guard on it. Not monotonic across a release: the row is
    /// deleted, so the next acquire of the same key restarts at 1.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
