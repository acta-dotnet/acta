using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// The hot mutable runtime state of one Job: one row in <c>runtimes</c> per <c>jobs</c> row, split
/// out so claim/complete churn never rewrites the append-mostly identity/input row. Every job state
/// transition (claim, start, complete, control verb, schedule firing) updates this row and bumps
/// <see cref="Version"/>, the CAS token for job state transitions. Execution ownership/TTL lives
/// here too (<see cref="LeasedByWorkerId"/> / <see cref="LeaseExpiresAtUtc"/>): a claim is one
/// UPDATE of this row, and the heartbeat pushes <see cref="LeaseExpiresAtUtc"/> without bumping
/// <see cref="Version"/>. The <c>leases</c> table carries named locks only.
/// </summary>
/// <remarks>
/// Carries <see cref="ModifiedAtUtc"/> but no <c>created_at_utc</c>, unlike every other timestamped
/// entity: the creation instant lives on the 1:1 <c>jobs</c> row instead (intentional omission).
/// </remarks>
[DbTable("runtimes")]
[DbPrimaryKey(Name = "pk_runtimes", Columns = ["job_id"], Manual = true, OptimizeForSequentialKey = true)]
[DbForeignKey(
    Name = "fk_runtimes_jobs",
    Target = typeof(Job),
    TargetColumn = "id",
    Column = "job_id",
    OnDelete = DbForeignKeyAction.Cascade
)]
[DbIndex(
    Name = "ix_runtimes_claim_ready",
    Columns = ["namespace_id", "priority_code", "next_run_at_utc", "job_id"],
    Descending = ["priority_code"],
    Filter = "status_code = 10",
    Usage = "claim_hot_path"
)]
[DbIndex(
    Name = "ix_runtimes_retention",
    Columns = ["namespace_id", "retention_until_utc", "job_id"],
    Filter = "retention_until_utc IS NOT NULL AND status_code IN (100, 200, 220)",
    Usage = "maintenance"
)]
[DbIndex(
    Name = "ix_runtimes_worker_inflight",
    Columns = ["leased_by_worker_id", "status_code"],
    Filter = "leased_by_worker_id IS NOT NULL AND status_code IN (40, 50)",
    Usage = "heartbeat"
)]
[DbCheck(
    Name = "ck_runtimes_lease_consistency",
    Sql = "(leased_by_worker_id IS NULL AND lease_expires_at_utc IS NULL) OR (leased_by_worker_id IS NOT NULL AND lease_expires_at_utc IS NOT NULL)"
)]
[DbCheck(Name = "ck_runtimes_counters", Sql = "execution_number >= 0 AND failure_count >= 0")]
internal sealed class JobRuntime : IEntity<long>
{
    /// <summary>
    /// Owning Job; primary key (1:1 with <c>jobs</c>) and CASCADE FK, so a purged job sweeps its
    /// runtime row in the same transaction. Supplied by enqueue, never DB-assigned.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// Immutable copy of the owning Job's namespace, denormalized so the hot claim, reclaim, and
    /// retention scans filter and seek without joining <c>jobs</c> (<c>ix_runtimes_claim_ready</c> /
    /// <c>ix_runtimes_retention</c> lead with it). Written once at insert, never updated.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int16)]
    public short NamespaceId { get; init; }

    /// <summary>
    /// Durable lifecycle of the Job (Paused / Suspended / Ready / Dispatched / Executing / Done / Failed / Cancelled).
    /// </summary>
    [DbColumn("status_code")]
    public JobStatusCode Status { get; set; }

    /// <summary>
    /// Claim-order key set from the definition policy, definition override, or per-enqueue override.
    /// </summary>
    [DbColumn("priority_code")]
    public JobPriorityCode Priority { get; set; }

    /// <summary>
    /// Next claim instant; the hot-path claim filter compares against this.
    /// </summary>
    [DbColumn("next_run_at_utc", DbKind.UtcInstant)]
    public DateTime? NextRunAtUtc { get; set; }

    /// <summary>
    /// Monotonic-lifetime claim counter; incremented atomically on each claim.
    /// </summary>
    [DbColumn("execution_number", DbKind.Int32)]
    public int ExecutionNumber { get; set; }

    /// <summary>
    /// Current-cycle failure counter; compared against <c>MaxAttempts</c>.
    /// </summary>
    [DbColumn("failure_count", DbKind.Int16)]
    public short FailureCount { get; set; }

    /// <summary>
    /// Worker that currently holds the in-flight execution lease, if any. No FK; write-time
    /// validation in the claim routine. Paired with <see cref="LeaseExpiresAtUtc"/> by
    /// <c>ck_runtimes_lease_consistency</c>.
    /// </summary>
    [DbColumn("leased_by_worker_id", DbKind.Int32)]
    public int? LeasedByWorkerId { get; set; }

    /// <summary>
    /// Execution lease expiry instant; the heartbeat pushes it forward without bumping
    /// <see cref="Version"/>, and <c>sys.recovery</c> reclaims in-flight rows past it.
    /// </summary>
    [DbColumn("lease_expires_at_utc", DbKind.UtcInstant)]
    public DateTime? LeaseExpiresAtUtc { get; set; }

    /// <summary>
    /// When <c>sys.retention</c> deletes the owning job row (this row cascades with it).
    /// </summary>
    [DbColumn("retention_until_utc", DbKind.UtcInstant)]
    public DateTime? RetentionUntilUtc { get; set; }

    /// <summary>When the runtime row was last updated. Set server-side on every mutation.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Optimistic-concurrency token for job state transitions; operations manually increment via
    /// <c>SET version = version + 1</c> on every UPDATE. Heartbeats never bump it: a lease TTL
    /// refresh is not a claim-generation change, so a buffered claim still passes the start CAS.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
