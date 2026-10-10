namespace Acta;

/// <summary>
/// One row in <c>acta.jobs</c>, Acta's only work unit: the append-mostly identity/input record.
/// Hot mutable state (status, next run, counters, retention) lives on the 1:1 <c>runtimes</c> row,
/// which also carries execution ownership/TTL (<c>locks</c> holds named locks only); result
/// history lives in <c>results</c>, keyed by the composite <c>(job_id, execution_number)</c>
/// clustered PK. Recurring Jobs are a single reusable row whose runtime <c>next_run_at_utc</c>
/// rolls forward on terminal completion (no per-firing row inflation).
/// </summary>
[DbTable("jobs")]
[DbPrimaryKey(Name = "pk_jobs", Columns = ["id"], OptimizeForSequentialKey = true)]
[DbUniqueIndex(Name = "ux_jobs_ref", Columns = ["job_ref"], Usage = "uniqueness")]
[DbUniqueIndex(
    Name = "ux_jobs_deduplication_key_root",
    Columns = ["namespace_id", "deduplication_key"],
    Filter = "deduplication_key IS NOT NULL AND parent_id IS NULL",
    Usage = "uniqueness"
)]
[DbUniqueIndex(
    Name = "ux_jobs_deduplication_key_child",
    Columns = ["parent_id", "deduplication_key"],
    Filter = "deduplication_key IS NOT NULL AND parent_id IS NOT NULL",
    Usage = "uniqueness"
)]
[DbIndex(Name = "ix_jobs_parent", Columns = ["parent_id"], Filter = "parent_id IS NOT NULL", Usage = "child_fanout")]
[DbIndex(
    Name = "ix_jobs_namespace_created",
    Columns = ["namespace_id", "created_at_utc", "id"],
    Descending = ["created_at_utc", "id"],
    Usage = "dashboard_grid"
)]
[DbCheck(Name = "ck_jobs_input_pair", Sql = "(input_format_id = 0 AND input IS NULL) OR (input_format_id <> 0 AND input IS NOT NULL)")]
internal sealed class JobEntity : IEntity<long>
{
    // ---------- Identity ----------

    /// <summary>
    /// Primary key and internal engine identity, used by every join, claim, event, and lock; the
    /// public handle is <see cref="JobRef"/>. Provider-native IDENTITY: the database assigns the id
    /// at INSERT, and <c>enqueue_jobs</c> correlates the <c>RETURNING</c>/<c>OUTPUT</c> rows back to
    /// their input via the caller-supplied <see cref="JobRef"/> (insertion order is not guaranteed).
    /// </summary>
    [DbColumn("id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// The public reference clients see in place of the numeric id ("job_" plus 26 Crockford Base32 characters),
    /// minted in C# as a UUIDv7 (<see cref="Acta.JobRef.New"/>) and never defaulted by the database. It is copied onto
    /// events and alerts so it survives a purge, and never used for joins, claim order, or keyset paging.
    /// </summary>
    [DbColumn("job_ref", DbKind.Guid)]
    public Guid JobRef { get; init; }

    // ---------- Scope / routing ----------

    /// <summary>
    /// Service-owned execution boundary and the hot-path claim filter. FK semantics enforced by SP, not DB.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// Surrogate key into <c>definitions</c>; keeps the hot row narrow.
    /// </summary>
    [DbColumn("definition_id", DbKind.Int32)]
    public int DefinitionId { get; init; }

    /// <summary>
    /// Identifies the lineage tree this Job belongs to. <c>NULL</c> for root jobs (the root is its own
    /// <see cref="Id"/>); children carry <c>parent.LineageRootId</c>. Set at INSERT, never mutated.
    /// Event-emitting routines resolve the effective root as <c>COALESCE(lineage_root_id, id)</c> so a
    /// root's own events still land on the <c>events</c> lineage timeline index.
    /// </summary>
    [DbColumn("lineage_root_id", DbKind.Int64)]
    public long? LineageRootId { get; init; }

    /// <summary>
    /// Causal parent for child jobs; null for root jobs. No FK; SP write-time validation.
    /// </summary>
    [DbColumn("parent_id", DbKind.Int64)]
    public long? ParentId { get; init; }

    /// <summary>
    /// Optional customer / business entity this Job is <em>about</em>, resolved from the enqueue
    /// <c>TenantName</c> against the <c>tenants</c> catalog. NULL when the enqueue supplied no tenant
    /// (and for system Jobs). Immutable after enqueue; child Jobs inherit the parent's tenant unless
    /// they supply their own. Audit / query / runtime scope only, never a claim or scheduling scope.
    /// </summary>
    [DbColumn("tenant_id", DbKind.Int32)]
    public int? TenantId { get; init; }

    // ---------- Caller keys ----------

    /// <summary>
    /// Deduplication key on enqueue and a stable address for <c>IJobs.GetJobIdAsync</c>, compared exactly; user keys
    /// cannot start with <c>"sys."</c> in any case. Root jobs are unique per namespace (<c>ux_jobs_deduplication_key_root</c>) and children per direct
    /// parent (<c>ux_jobs_deduplication_key_child</c>). A recurring slot takes the job's name as its key, so a root
    /// enqueue keyed by that name deduplicates onto the slot.
    /// </summary>
    [DbColumn("deduplication_key", DbKind.AsciiString, Size = 128, CaseExact = true)]
    public string? DeduplicationKey { get; init; }

    /// <summary>
    /// Opaque correlation key threading related jobs across systems: a W3C trace id or a caller's own
    /// custom value. At most 64 chars; null = uncorrelated.
    /// </summary>
    [DbColumn("correlation_key", DbKind.AsciiString, Size = 64)]
    public string? CorrelationKey { get; init; }

    /// <summary>
    /// Named concurrency key, a case-sensitive key like the deduplication key. Enforced by execution-time slot leases
    /// (<c>{ns_id}.sem.{key}.{slot}</c> rows) the runner takes after claim; a claimed loser re-arms
    /// Ready after the fixed bounce delay. The key alone admits one at a time; the definition's
    /// <c>concurrency_limit</c> widens it to that many slots.
    /// </summary>
    [DbColumn("concurrency_key", DbKind.AsciiString, Size = 128, CaseExact = true)]
    public string? ConcurrencyKey { get; init; }

    // ---------- Input ----------

    /// <summary>
    /// Format-id selector for this row's input. <c>0</c> means no input (<c>JobPayloadFormat.None</c>);
    /// the <c>ck_jobs_input_pair</c> CHECK enforces that <c>input_format_id = 0</c> holds exactly when
    /// <c>input IS NULL</c>.
    /// </summary>
    [DbColumn("input_format_id", DbKind.Byte)]
    public byte InputFormatId { get; init; }

    /// <summary>
    /// Encoded input payload. NULL when <see cref="InputFormatId"/> is <c>0</c> (no input);
    /// non-NULL otherwise (may be an empty byte sequence for legitimately empty
    /// <c>text</c>/<c>bytes</c> payloads). Format governed by <see cref="InputFormatId"/>.
    /// </summary>
    [DbColumn("input", DbKind.BinaryPayload)]
    public byte[]? Input { get; init; }

    // ---------- Audit emission ----------

    /// <summary>
    /// Per-job snapshot of the definition's audit level, copied from <c>definitions</c> at
    /// enqueue. Gates audit-filtered per-job <c>JobEvent</c> emission on the hot path
    /// (claim / start / complete / control) without a join: <c>Off</c> suppresses them,
    /// <c>Failures</c> emits only failed <c>job.execution-finished</c>, <c>Audit</c> emits all.
    /// Always-on system / catalog events ignore this column.
    /// </summary>
    [DbColumn("audit_level_code")]
    public DefinitionAuditLevelCode AuditLevel { get; init; }

    // ---------- Audit ----------

    /// <summary>
    /// When the Job was enqueued. Set server-side.
    /// </summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }
}
