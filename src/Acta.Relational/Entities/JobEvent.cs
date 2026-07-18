using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// Append-only lifecycle timeline and execution ledger. Carries the audit trail of <c>JobRuntime.Status</c>
/// transitions, suspend/resume hops, signal arrivals, and definition/worker lifecycle events, plus the
/// canonical per-attempt record via paired <c>job.execution.started</c> / <c>job.execution.finished</c>
/// events. Carries lifecycle facts and outcomes, plus an optional <see cref="Detail"/> payload for
/// free-form (text) or structured (json) context beyond <see cref="ReasonCode"/> /
/// <see cref="ReasonMessage"/>; richer event-specific data still belongs in OTel spans and structured
/// logs at the emission site, and the canonical durable state is the entity row the event references.
/// </summary>
/// <remarks>
/// Every row honors <c>JobsOptions.JobEventsRetentionDays</c> (default 365); the
/// <c>sys.retention</c> sweep applies one predicate against <see cref="CreatedAtUtc"/>.
/// There is no enforced <c>Job</c> FK because events outlive Job retention; integrity is maintained at
/// write time by the operations that emit events.
/// </remarks>
[DbTable("events", PageCompression = true)]
[DbPrimaryKey(Name = "pk_events", Columns = ["id"], OptimizeForSequentialKey = true)]
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
internal sealed class JobEvent : IEntity<long>
{
    /// <summary>
    /// Event row identifier.
    /// </summary>
    [DbColumn("id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// What happened; the numeric id decodes via the <c>event</c> family in docs/98, e.g.
    /// <c>41 = "job.execution.finished"</c>. Closed taxonomy.
    /// </summary>
    [DbColumn("event_code")]
    public JobEventCode EventCode { get; init; }

    /// <summary>
    /// When the event was committed; rendered server-side via <see cref="DbDefault.UtcNow"/> in the same
    /// transaction as the state mutation. The operation does not supply this value from C#. Named
    /// <c>created_at_utc</c> for parity with every other entity's row-creation timestamp; for events, the
    /// row-creation instant IS the event-occurrence instant (events are insert-only).
    /// </summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>
    /// Namespace this event belongs to; per-namespace timeline queries seek without joining.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int16)]
    public short NamespaceId { get; init; }

    /// <summary>
    /// Who caused the transition (<c>Sys</c> / <c>Worker</c> / <c>Operator</c> / <c>Job</c>).
    /// System-determined at the emission site; callers cannot pass it directly.
    /// </summary>
    [DbColumn("actor_code")]
    public JobActorCode ActorCode { get; init; }

    /// <summary>
    /// Identifier of the actor whose <see cref="ActorCode"/> classifies it. Format depends on
    /// <see cref="ActorCode"/>; see the <see cref="JobActorCode"/> doc. A string identifier by design
    /// (its format varies by <see cref="ActorCode"/>), an accepted exception to the integer-<c>_id</c>
    /// convention.
    /// </summary>
    [DbColumn("actor_key", DbKind.AsciiString, Size = 128)]
    public string? ActorKey { get; init; }

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
    /// Which attempt this event belongs to; copied from <c>JobRuntime.ExecutionNumber</c> at emission. Set on
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
    /// Surrogate key into <c>JobDefinition</c>. Set on every job-scoped event; supports analytics that
    /// group by Job type without joining <c>Job</c> (which is retention-deletable). Null on
    /// worker-lifecycle events.
    /// </summary>
    [DbColumn("definition_id", DbKind.Int32)]
    public int? DefinitionId { get; init; }

    /// <summary>
    /// Tenant of the owning Job; copied from <c>Job.TenantId</c> at emission for job-scoped events. NULL
    /// when the Job has no tenant, and on definition / worker / catalog events. Powers tenant-scoped audit
    /// queries without joining <c>Job</c> (which is retention-deletable).
    /// </summary>
    [DbColumn("tenant_id", DbKind.Int32)]
    public int? TenantId { get; init; }

    /// <summary>
    /// Worker that owned the transition; set on claim / execution / heartbeat-derived events. No FK.
    /// Null on definition events and on Job-level transitions without a worker context.
    /// </summary>
    [DbColumn("worker_id", DbKind.Int32)]
    public int? WorkerId { get; init; }

    /// <summary>
    /// Pre-transition <c>JobRuntime.Status</c>; null for non-Status-transition events.
    /// </summary>
    [DbColumn("from_status_code")]
    public JobStatusCode? FromStatus { get; init; }

    /// <summary>
    /// Post-transition <c>JobRuntime.Status</c>; null for non-Status-transition events.
    /// </summary>
    [DbColumn("to_status_code")]
    public JobStatusCode? ToStatus { get; init; }

    /// <summary>
    /// Per-execution outcome for <c>job.execution.finished</c> events; null otherwise. The failure budget
    /// on <c>JobRuntime.FailureCount</c> charges only on <c>Failed</c> / <c>Orphaned</c>.
    /// </summary>
    [DbColumn("execution_status_code")]
    public ExecutionStatusCode? ExecutionStatus { get; init; }

    /// <summary>
    /// Wall-clock duration of the attempt in milliseconds; written on <c>job.execution.finished</c>
    /// (computed in code from start to finish, not from event timestamps, so it tolerates clock skew
    /// between operation invocations). NULL on every other event type.
    /// <c>JobDefinition.ExecutionTimeoutSeconds</c> carries a CHECK that keeps attempt durations within
    /// this int column's representable range.
    /// </summary>
    [DbColumn("duration_ms", DbKind.Int32)]
    public int? DurationMs { get; init; }

    /// <summary>
    /// Machine-readable reason captured at the transition; NULL on success outcomes. Operators JOIN
    /// the <c>job-event-reason</c> family in docs/98 for the kebab string.
    /// </summary>
    [DbColumn("reason_code")]
    public JobEventReasonCode? ReasonCode { get; init; }

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
