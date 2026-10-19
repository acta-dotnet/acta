namespace Acta;

/// <summary>
/// The append-only lifecycle ledger: status transitions, suspend and resume hops, signal arrivals, definition and
/// worker lifecycle, and each attempt's outcome as a <c>job.execution-finished</c> event, with an optional
/// <see cref="Detail"/> beyond <see cref="ReasonCode"/> and <see cref="ReasonMessage"/>.
/// Rows age out by <c>JobsOptions.JobEventsRetention</c> against <see cref="CreatedAtUtc"/>; there is no job foreign
/// key, because events outlive the job, so the operations that write them keep them consistent.
/// </summary>
[DbTable("events", PageCompression = true)]
// Keyed by worker first, so concurrent writers insert into their own key ranges instead of all contending for the
// last page of one sequence. id stays the one global identity. No index leads with id on SQL Server or PostgreSQL,
// so a read or delete that needs one row names the pair or walks a timeline index; SQLite, with one writer, keeps
// id as its rowid key.
[DbPrimaryKey(Name = "pk_events", Columns = ["worker_id", "id"], Identity = "id", OptimizeForSequentialKey = true)]
[DbIndex(
    Name = "ix_events_lineage_timeline",
    Columns = ["lineage_root_id", "created_at_utc", "id"],
    Filter = "lineage_root_id IS NOT NULL",
    Usage = "read_api"
)]
[DbIndex(Name = "ix_events_namespace_timeline", Columns = ["namespace_id", "event_code", "created_at_utc", "id"], Usage = "read_api")]
[DbIndex(Name = "ix_events_job_timeline", Columns = ["job_id", "created_at_utc", "id"], Filter = "job_id IS NOT NULL", Usage = "read_api")]
[DbIndex(
    Name = "ix_events_namespace_created",
    Columns = ["namespace_id", "created_at_utc", "id"],
    Descending = ["created_at_utc", "id"],
    Usage = "dashboard_grid"
)]
[DbCheck(
    Name = "ck_events_detail_pair",
    Sql = "(detail_format_id = 0 AND detail IS NULL) OR (detail_format_id <> 0 AND detail IS NOT NULL)"
)]
internal sealed class EventEntity : IEntity<long>
{
    /// <summary>
    /// Event row identifier.
    /// </summary>
    [DbColumn("id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// What happened; the numeric id decodes via the <c>event</c> family in code-families.md, e.g.
    /// <c>41 = "job.execution-finished"</c>. Closed taxonomy.
    /// </summary>
    [DbColumn("event_code")]
    public EventCode EventCode { get; init; }

    /// <summary>
    /// When the event was written; rendered server-side via <see cref="DbDefault.UtcNow"/> in the same
    /// transaction as the state mutation. The operation does not supply this value from C#. It is not a
    /// commit instant and does not order commits: PostgreSQL's <c>now()</c> is transaction start and SQL
    /// Server's <c>SYSUTCDATETIME()</c> is statement time, so a long transaction commits after its own
    /// stamp. Named <c>created_at_utc</c> for parity with every other entity's row-creation timestamp;
    /// for events, the row-creation instant IS the event-occurrence instant (events are insert-only).
    /// </summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// Namespace this event belongs to; per-namespace timeline queries seek without joining.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// Who caused the transition (<c>Sys</c> / <c>Worker</c> / <c>Operator</c> / <c>Job</c>).
    /// System-determined at the emission site; callers cannot pass it directly.
    /// </summary>
    [DbColumn("actor_code")]
    public ActorCode ActorCode { get; init; }

    /// <summary>
    /// Identifier of the actor whose <see cref="ActorCode"/> classifies it. Format depends on
    /// <see cref="ActorCode"/>; see the <see cref="ActorCode"/> doc. A string identifier by design
    /// (its format varies by <see cref="ActorCode"/>), an accepted exception to the integer-<c>_id</c>
    /// convention. Unicode, unlike the other key columns: for an operator it carries the
    /// authenticated principal's name, and an audit trail that folds two names differing only by a
    /// diacritic onto one spelling cannot say who acted.
    /// </summary>
    [DbColumn("actor", DbKind.UnicodeString, Size = 128)]
    public string? Actor { get; init; }

    // No enforced FKs: audit outlives Job and JobAlert retention, so references here are logical only.

    /// <summary>
    /// Owning Job (logical reference; no DB constraint, events outlive Job retention). Set for
    /// job-scoped events, null for definition / worker events. It stays the stable timeline key after a
    /// Job row is purged: identity ids are never reused, so <c>job_id</c> remains a unique address even
    /// once the row it referenced is gone.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long? JobId { get; init; }

    /// <summary>
    /// Public ref of the owning Job, denormalized at emission so the PUBLIC identity survives Job purge:
    /// <see cref="JobId"/> stays the internal address, this stays the public one operators and clients
    /// hold. NULL on definition / worker events, mirroring <see cref="JobId"/>.
    /// </summary>
    [DbColumn("job_ref", DbKind.Guid)]
    public Guid? JobRef { get; init; }

    /// <summary>
    /// Which attempt this event belongs to; copied from <c>runtimes.execution_number</c> at emission. Set on
    /// <c>job.execution.*</c>, <c>job.step.*</c>, and other per-attempt events; NULL for definition,
    /// worker, and Job-level transitions without an attempt context.
    /// </summary>
    [DbColumn("execution_number", DbKind.Int32)]
    public int? ExecutionNumber { get; init; }

    /// <summary>
    /// Root of the lineage tree this event belongs to; powers whole-lineage timeline queries via
    /// <c>ix_events_lineage_timeline</c>. Null for definition / worker events.
    /// </summary>
    [DbColumn("lineage_root_id", DbKind.Int64)]
    public long? LineageRootId { get; init; }

    /// <summary>
    /// Surrogate key into <c>definitions</c>. Set on every job-scoped event; supports analytics that
    /// group by Job type without joining <c>Job</c> (which is retention-deletable). Null on
    /// worker-lifecycle events.
    /// </summary>
    [DbColumn("definition_id", DbKind.Int32)]
    public int? DefinitionId { get; init; }

    /// <summary>
    /// Tenant of the owning Job; copied from <c>jobs.tenant_id</c> at emission for job-scoped events. NULL
    /// when the Job has no tenant, and on definition / worker / catalog events. Powers tenant-scoped audit
    /// queries without joining <c>Job</c> (which is retention-deletable).
    /// </summary>
    [DbColumn("tenant_id", DbKind.Int32)]
    public int? TenantId { get; init; }

    /// <summary>
    /// Worker that owned the transition; set on claim / execution / heartbeat-derived events. No FK.
    /// 0 on definition events and on Job-level transitions without a worker context: worker ids start
    /// at 1, and the default lets those writes leave the column out.
    /// </summary>
    [DbColumn("worker_id", DbKind.Int32, Default = DbDefault.Zero)]
    public int WorkerId { get; init; }

    /// <summary>
    /// Pre-transition <c>runtimes.status_code</c>; null for non-Status-transition events.
    /// </summary>
    [DbColumn("from_status_code")]
    public JobStatusCode? FromStatus { get; init; }

    /// <summary>
    /// Post-transition <c>runtimes.status_code</c>; null for non-Status-transition events.
    /// </summary>
    [DbColumn("to_status_code")]
    public JobStatusCode? ToStatus { get; init; }

    /// <summary>
    /// Per-execution outcome for <c>job.execution-finished</c> events; null otherwise. The failure budget
    /// on <c>runtimes.failure_count</c> charges only on <c>Failed</c> / <c>Orphaned</c>.
    /// </summary>
    [DbColumn("execution_status_code")]
    public ExecutionStatusCode? ExecutionStatus { get; init; }

    /// <summary>
    /// Wall-clock duration of the attempt in milliseconds; written on <c>job.execution-finished</c>
    /// (computed in code from start to finish, not from event timestamps, so it tolerates clock skew
    /// between operation invocations). NULL on every other event type.
    /// Registration caps the execution timeout and the worker clamps the measurement, so a duration always fits
    /// this int column.
    /// </summary>
    [DbColumn("duration_ms", DbKind.Int32)]
    public int? DurationMs { get; init; }

    /// <summary>
    /// Machine-readable reason captured at the transition; NULL on success outcomes. Operators JOIN
    /// the <c>job-event-reason</c> family in code-families.md for the kebab string.
    /// </summary>
    [DbColumn("reason_code")]
    public EventReasonCode? ReasonCode { get; init; }

    /// <summary>
    /// Free-form prose paired with <see cref="ReasonCode"/>; truncated by <c>MessageTruncator</c>.
    /// </summary>
    [DbColumn("reason_message", DbKind.UnicodeString, Size = 512)]
    public string? ReasonMessage { get; init; }

    /// <summary>
    /// Format-id selector for <see cref="Detail"/>; <c>0</c> means no detail payload. Defaults to 0 so
    /// events that carry no detail need not name the column. <c>ck_events_detail_pair</c> enforces
    /// <c>(detail_format_id = 0) = (detail IS NULL)</c>; operator views decode formats 1 (json) /
    /// 3 (text) as UTF-8 text, while other formats stay opaque.
    /// </summary>
    [DbColumn("detail_format_id", DbKind.Byte, Default = DbDefault.Zero)]
    public byte DetailFormatId { get; init; }

    /// <summary>
    /// Optional free-form (text) or structured (json) context beyond <see cref="ReasonCode"/> /
    /// <see cref="ReasonMessage"/>; opaque encoded bytes. NULL when <see cref="DetailFormatId"/> is 0.
    /// </summary>
    [DbColumn("detail", DbKind.BinaryPayload)]
    public byte[]? Detail { get; init; }
}
