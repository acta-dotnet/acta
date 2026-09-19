using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// One row per named lock in the <c>locks</c> table: the rows behind the handler-facing
/// <c>JobContext.RunWithLockAsync</c>, the concurrency slots the runner takes after claim, and the
/// rate meter's bucket and reservation rows, which live here but are never held.
/// Execution ownership/TTL is not a row here - it lives on the <c>runtimes</c> row.
/// Lifecycle is <see cref="ExpiresAtUtc"/> alone (no status column): held while ahead of now.
/// Acquire is a steal-on-expiry upsert; release DELETEs the row - concurrency keys are unbounded
/// per-job user strings, so the table stays O(currently held) by construction
/// (<c>ReleaseLockSpec</c> pins that); abandoned rows are swept by the <c>sys.retention</c> reap.
/// <see cref="HoldToken"/> is minted fresh per hold and CAS-guards extend and release: unlike a
/// counter, no other hold can ever re-mint it, so a stale holder that slept through a full
/// steal-release-reacquire cycle still cannot free or extend its successor's lock.
/// <see cref="LockKey"/> is an opaque discriminator-segmented composite so the lock spaces never
/// collide on identical user text; keying on namespace id keeps the key compact and stable across
/// renames. A concurrency key with limit N owns slots 0..N-1, one row each.
/// </summary>
[DbTable("locks")]
[DbPrimaryKey(Name = "pk_locks", Columns = ["lock_key"])]
[DbIndex(Name = "ix_locks_reclaim_expired", Columns = ["expires_at_utc"], Usage = "lock_reclaim")]
internal sealed class Lock : IEntity
{
    /// <summary>
    /// Opaque composite lock identity and primary key. Holds a discriminator-segmented string
    /// (<c>{ns}.lock.{key}</c> / <c>global.lock.{key}</c> / <c>{ns}.sem.{key}.{slot}</c> /
    /// <c>{ns}.rate.{key}</c> / <c>{ns}.rate.{key}.{job_id}</c>); never parsed for
    /// safety, since keys are compared as whole strings.
    /// </summary>
    [DbColumn("lock_key", DbKind.AsciiString, Size = 256)]
    public string LockKey { get; init; } = default!;

    /// <summary>
    /// Job that currently holds (or last held) the lock. No FK; written by the acquire routine for
    /// observability ("which job holds this lock"). Release is token-CAS on the PK, not on this column.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; set; }

    /// <summary>
    /// Instant the current hold expires. Held while ahead of now; free once at or before now (stealable
    /// in place via steal-on-expiry). Release deletes the row rather than expiring it.
    /// On the two <c>rate.</c> shapes this is an instant rather than a lease: the turn one job was
    /// given on a reservation row, and on a bucket row the meter's next theoretical arrival time
    /// shifted forward by the burst, so the row expires exactly when an idle meter stops differing
    /// from a missing one. The expiry sweep therefore restores an idle meter to its full burst without
    /// ever disturbing a busy one, and drops a turn nobody spent.
    /// </summary>
    [DbColumn("expires_at_utc", DbKind.UtcInstant)]
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Per-hold CAS token, minted by the caller on every acquire (fresh insert and steal alike).
    /// Extend and release guard on it, so only the current holder can do either. The rate meter's rows
    /// carry <c>Guid.Empty</c>, the sentinel for "no holder": nothing extends or releases them.
    /// </summary>
    [DbColumn("hold_token", DbKind.Guid)]
    public Guid HoldToken { get; set; }
}
