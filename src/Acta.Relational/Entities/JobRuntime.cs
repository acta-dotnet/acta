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
// Covers both claimable statuses: Ready rows, and Suspended rows carrying a durable wait's expiration
// in next_run_at_utc. A Suspended row with a NULL next_run_at_utc is an unbounded wait and stays
// unclaimable, which the claim predicate enforces; the index only has to admit the candidates.
// status_code trails the seek and sort keys purely to keep the claim covering: admitting two statuses
// means the predicate has to tell them apart, and without the column in the index every candidate row
// costs a heap lookup on the hottest query in the system.
[DbIndex(
    Name = "ix_runtimes_claim_ready",
    Columns = ["namespace_id", "priority_code", "next_run_at_utc", "job_id", "status_code"],
    Descending = ["priority_code"],
    Filter = "status_code IN (10, 20)",
    Usage = "claim_hot_path"
)]
// OptimizeForSequentialKey: retention instants ascend, so completions insert at this index's tail; a
// per-index page-latch attribution measured 9.5 s of wait on that tail page at 16 workers.
[DbIndex(
    Name = "ix_runtimes_retention",
    Columns = ["namespace_id", "retention_until_utc", "job_id"],
    Filter = "retention_until_utc IS NOT NULL AND status_code IN (100, 200, 220)",
    Usage = "maintenance",
    OptimizeForSequentialKey = true
)]
// status_code stays in the filter but out of the key, which is all the heartbeat needs: keying on it
// would move every in-flight entry to a new key on the Dispatched-to-Executing transition, and one
// worker's entries share a leaf page, so that write lands on this index's hottest page.
[DbIndex(
    Name = "ix_runtimes_worker_inflight",
    Columns = ["leased_by_worker_id", "job_id"],
    Filter = "leased_by_worker_id IS NOT NULL AND status_code IN (40, 50)",
    Usage = "heartbeat"
)]
[DbCheck(
    Name = "ck_runtimes_lease_consistency",
    Sql = "(leased_by_worker_id IS NULL AND lease_expires_at_utc IS NULL) OR (leased_by_worker_id IS NOT NULL AND lease_expires_at_utc IS NOT NULL)"
)]
[DbCheck(Name = "ck_runtimes_counters", Sql = "execution_number >= 0 AND failure_count >= 0")]
// Both directions, so with ck_runtimes_lease_consistency an in-flight status and a complete lease pair
// are the same fact. ClaimOne, ClaimBatch and StartExecution are the only routines that enter 40/50,
// and each writes the lease in the same statement. Every routine that nulls the lease leaves 40/50 in
// the same statement: CompleteExecution, CompleteExecutionsBatch, ReclaimStuckJobs, RepairRecoverySlot,
// CancelJob, RestartJob.
[DbCheck(Name = "ck_runtimes_status_lease", Sql = "status_code IN (40, 50) OR leased_by_worker_id IS NULL")]
[DbCheck(Name = "ck_runtimes_inflight_leased", Sql = "status_code NOT IN (40, 50) OR leased_by_worker_id IS NOT NULL")]
// A Ready row is due at a known instant, which is what lets the claim seek ix_runtimes_claim_ready in
// its own order instead of sorting every ready row. Every routine that lands Ready writes the instant
// in the same statement: EnqueueOne, EnqueueBatch, RegisterScheduledJobs, RestartJob, ResumeJob,
// RescheduleJob, TriggerScheduleNow, RaiseSignal, CompleteExecution (re-arm and signal release),
// ReclaimStuckJobs, RepairRecoverySlot. NULL is reserved for a Suspended row's unbounded wait.
[DbCheck(Name = "ck_runtimes_ready_due", Sql = "status_code <> 10 OR next_run_at_utc IS NOT NULL")]
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
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// Durable lifecycle of the Job (Paused / Suspended / Ready / Dispatched / Executing / Succeeded / Failed / Cancelled).
    /// </summary>
    [DbColumn("status_code")]
    public JobStatusCode Status { get; set; }

    /// <summary>
    /// Claim-order key set from the definition policy, definition override, or per-enqueue override.
    /// </summary>
    [DbColumn("priority_code")]
    public JobPriorityCode Priority { get; set; }

    /// <summary>
    /// Next claim instant; the hot-path claim filter compares against this. A <c>Ready</c> row always
    /// carries it, enforced by <c>ck_runtimes_ready_due</c>. On a <c>Suspended</c> row it carries the
    /// awaited slot's expiration, or NULL for an unbounded wait, which is what keeps an unbounded wait
    /// unclaimable while a bounded one wakes at its deadline.
    /// </summary>
    [DbColumn("next_run_at_utc", DbKind.UtcInstant)]
    public DateTime? NextRunAtUtc { get; set; }

    /// <summary>
    /// Monotonic-lifetime claim counter; incremented atomically on each claim. It follows claims, not
    /// scheduled occurrences, so retries and reclaims advance it too. Int32 is a deliberate width: a
    /// recurring slot claimed once a second for its whole life would take about sixty-eight years to
    /// exhaust it, and exhaustion lies outside the supported lifetime of one durable slot. The
    /// providers raise on overflow rather than wrap (SQLite stores a wider integer, and the Int32
    /// mapper rejects it on read), so the failure mode is an error, never a negative attempt number.
    /// </summary>
    [DbColumn("execution_number", DbKind.Int32)]
    public int ExecutionNumber { get; set; }

    /// <summary>
    /// Failure counter for the current cycle, compared against <c>MaxAttempts</c> on a one-off job. A
    /// recurring slot is not terminalized for crossing that budget, so its counter keeps climbing across
    /// occurrences and every increment path saturates at <see cref="short.MaxValue"/> rather than
    /// overflowing: at the ceiling the value means "that many or more", not an exact lifetime count.
    /// </summary>
    [DbColumn("failure_count", DbKind.Int16)]
    public short FailureCount { get; set; }

    /// <summary>
    /// Worker that currently holds the in-flight execution lease, if any. No FK; write-time
    /// validation in the claim routine. Paired with <see cref="LeaseExpiresAtUtc"/> by
    /// <c>ck_runtimes_lease_consistency</c>, and bound to <see cref="Status"/> in both directions by
    /// <c>ck_runtimes_status_lease</c> and <c>ck_runtimes_inflight_leased</c>: set exactly while the
    /// status is <c>Dispatched</c> or <c>Executing</c>, NULL otherwise.
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
    /// Int32 is a deliberate width, and the same token type is the public <c>expectedVersion</c>. A
    /// continuously successful recurring slot firing once a second, at the normal three increments
    /// per occurrence (claim, start, complete), would take about twenty-two years to exhaust it;
    /// retries, recovery, and operator controls consume versions faster, and exhaustion lies outside
    /// the supported lifetime of one durable slot. The providers raise on overflow rather than wrap,
    /// so a token can never come back negative.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
