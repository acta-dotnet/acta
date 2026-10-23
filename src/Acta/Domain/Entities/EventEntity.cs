namespace Acta;

/// <summary>
/// The append-only ledger: what happened to an entity, named by <see cref="EventCode"/> and the
/// <see cref="EntityCode"/>/<see cref="EntityId"/> subject pair, with a human <see cref="EventMessage"/> and a small
/// encoded <see cref="Details"/> payload for the facts the code does not imply. Rows age out by
/// <c>JobsOptions.JobEventsRetention</c> against <see cref="CreatedAtUtc"/>; no foreign key binds the subject, because
/// events outlive it, so the operations that write them keep them consistent.
/// </summary>
[DbTable("events", PageCompression = true)]
// Keyed by worker first, so concurrent writers insert into their own key ranges instead of all contending for the
// last page of one sequence. event_id stays the one global identity. No index leads with it on SQL Server or
// PostgreSQL, so a read or delete that needs one row names the pair or walks a timeline index; SQLite, with one
// writer, keeps event_id as its rowid key.
[DbPrimaryKey(Name = "pk_events", Columns = ["worker_id", "event_id"], Identity = "event_id", OptimizeForSequentialKey = true)]
[DbIndex(
    Name = "ix_events_lineage_timeline",
    Columns = ["lineage_root_job_id", "created_at_utc", "event_id"],
    Filter = "lineage_root_job_id IS NOT NULL",
    Usage = "read_api"
)]
[DbIndex(Name = "ix_events_namespace_timeline", Columns = ["namespace_id", "event_code", "created_at_utc", "event_id"], Usage = "read_api")]
[DbIndex(Name = "ix_events_entity_timeline", Columns = ["entity_code", "entity_id", "created_at_utc", "event_id"], Usage = "read_api")]
[DbIndex(
    Name = "ix_events_namespace_created",
    Columns = ["namespace_id", "created_at_utc", "event_id"],
    Descending = ["created_at_utc", "event_id"],
    Usage = "dashboard_grid"
)]
[DbIndex(
    Name = "ix_events_duration",
    Columns = ["namespace_id", "definition_id", "created_at_utc"],
    Include = ["event_code", "duration_ms"],
    Filter = "event_code IN (40, 41, 44) AND duration_ms IS NOT NULL",
    Usage = "dashboard"
)]
[DbCheck(
    Name = "ck_events_details_pair",
    Sql = "(details_format_id = 0 AND details IS NULL) OR (details_format_id <> 0 AND details IS NOT NULL)"
)]
internal sealed class EventEntity : IEntity<long>
{
    /// <summary>Event row identifier, allocated independently of the history retention keeps.</summary>
    [DbColumn("event_id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// Namespace this event belongs to; per-namespace timeline queries seek without joining. Tenant events
    /// belong to the sys namespace.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// Worker that wrote the event: an attempt's completion, a note, a worker lifecycle event, or the stale holder
    /// of a reclaimed lease. No FK. 0 when no worker acted: worker ids start at 1, and the default lets those writes
    /// leave the column out.
    /// </summary>
    [DbColumn("worker_id", DbKind.Int32, Default = DbDefault.Zero)]
    public int WorkerId { get; init; }

    /// <summary>
    /// When the event was written; rendered server-side via <see cref="DbDefault.UtcNow"/> in the same
    /// transaction as the state mutation. It is not a commit instant and does not order commits: PostgreSQL's
    /// <c>now()</c> is transaction start and SQL Server's <c>SYSUTCDATETIME()</c> is statement time, so a long
    /// transaction commits after its own stamp.
    /// </summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Kind of the subject the event is about; with <see cref="EntityId"/> it names one row of any table.</summary>
    [DbColumn("entity_code")]
    public EntityCode EntityCode { get; init; }

    /// <summary>
    /// The subject's numeric identity. It stays a unique address after the subject row is gone, because identity
    /// ids are never reused.
    /// </summary>
    [DbColumn("entity_id", DbKind.Int64)]
    public long EntityId { get; init; }

    /// <summary>What happened; decodes via the <c>event</c> family in code-families.md. Closed taxonomy.</summary>
    [DbColumn("event_code")]
    public EventCode EventCode { get; init; }

    /// <summary>
    /// The job's definition on job-subject events and the subject itself on definition events; supports
    /// analytics that group by job type without joining <c>jobs</c>, which retention deletes.
    /// </summary>
    [DbColumn("definition_id", DbKind.Int32)]
    public int? DefinitionId { get; init; }

    /// <summary>
    /// Tenant of the job on job-subject events, copied from <c>jobs.tenant_id</c> at emission; null when the job has
    /// none, and on tenant events, whose subject carries it.
    /// </summary>
    [DbColumn("tenant_id", DbKind.Int32)]
    public int? TenantId { get; init; }

    /// <summary>
    /// Root of the job's lineage tree on job-subject events, the job itself for a root; powers whole-lineage
    /// timelines via <c>ix_events_lineage_timeline</c>.
    /// </summary>
    [DbColumn("lineage_root_job_id", DbKind.Int64)]
    public long? LineageRootJobId { get; init; }

    /// <summary>The attempt an attempt fact or note belongs to, copied from <c>jobs_state.execution_number</c>.</summary>
    [DbColumn("execution_number", DbKind.Int32)]
    public int? ExecutionNumber { get; init; }

    /// <summary>
    /// Wall-clock duration of a succeeded, failed, or yielded attempt in milliseconds, measured in code from start
    /// to finish; null otherwise, and on an orphaned attempt, which has no measured end. Registration caps the
    /// execution timeout and the worker clamps the measurement, so a duration always fits this int column.
    /// </summary>
    [DbColumn("duration_ms", DbKind.Int32)]
    public int? DurationMs { get; init; }

    /// <summary>Human text only, never a classification channel: a failure's message, an operator's note.</summary>
    [DbColumn("event_message", DbKind.UnicodeString, Size = 512)]
    public string? EventMessage { get; init; }

    /// <summary>
    /// Format id of <see cref="Details"/>; <c>0</c> means no payload. <c>ck_events_details_pair</c> enforces
    /// <c>(details_format_id = 0) = (details IS NULL)</c>; operator views decode formats 1 (json) and 3 (text) as
    /// UTF-8 text, while other formats stay opaque.
    /// </summary>
    [DbColumn("details_format_id", DbKind.Byte, Default = DbDefault.Zero)]
    public byte DetailsFormatId { get; init; }

    /// <summary>
    /// The facts the code does not imply, encoded by the writer: JSON by default, with keys such as
    /// <c>reason</c>, <c>actor</c>, <c>to</c>, and <c>schedule</c>. Small and rare; never job payload bytes.
    /// </summary>
    [DbColumn("details", DbKind.BinaryPayload)]
    public byte[]? Details { get; init; }
}
