namespace Acta;

/// <summary>
/// One named lock: the handler's <c>RunWithLockAsync</c> rows, the runner's concurrency slots (<c>{key}.0</c> to
/// <c>{key}.{N-1}</c>), and the rate meter's bucket and reservation rows, which are never held. A row is held while
/// <see cref="ExpiresAtUtc"/> is ahead of now: acquire steals an expired row, release deletes it, and retention reaps
/// abandoned ones, so the table holds only current holds. <see cref="HoldToken"/> is minted per hold and guards
/// extend and release, so a stale holder can never touch its successor's lock. <see cref="LockKey"/> is segmented by
/// lock space and keyed on namespace id, so spaces never collide and keys survive renames.
/// </summary>
[DbTable("locks")]
[DbPrimaryKey(Name = "pk_locks", Columns = ["lock_key"])]
[DbIndex(Name = "ix_locks_reclaim_expired", Columns = ["expires_at_utc"], Usage = "lock_reclaim")]
internal sealed class LockEntity : IEntity
{
    /// <summary>
    /// Opaque composite lock identity and primary key. Holds a discriminator-segmented string
    /// (<c>{ns}.lock.{key}</c> / <c>global.lock.{key}</c> / <c>{ns}.sem.{key}.{slot}</c> /
    /// <c>{ns}.rate.{key}</c> / <c>{ns}.rate.{key}.{job_id}</c>); never parsed for
    /// safety, since keys are compared as whole strings.
    /// </summary>
    [DbColumn("lock_key", DbKind.AsciiString, Size = 256, CaseExact = true)]
    public string LockKey { get; init; } = default!;

    /// <summary>
    /// Job that currently holds (or last held) the lock. No FK; written by the acquire routine for
    /// observability ("which job holds this lock"). Release is token-CAS on the PK, not on this column.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; set; }

    /// <summary>
    /// Instant the current hold expires: held while ahead of now, stealable once at or before it. On the rate
    /// meter's rows it is shifted to when the row stops mattering: a bucket expires when an idle meter equals a
    /// missing one, a reservation once its job cannot still be alive (its turn plus a grace past any lease). The sweep
    /// thus refills an idle meter without disturbing a busy one; a job whose swept turn returns is simply re-metered.
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
