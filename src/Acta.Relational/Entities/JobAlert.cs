using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// One materialized alert. Rows carrying a non-null <see cref="DedupeKey"/> collapse repeats inside the
/// window onto a single row (incrementing <see cref="OccurrenceCount"/>); the unique
/// <c>(namespace_id, dedupe_key, dedupe_window_start_utc)</c> index is the rate limit, and a null
/// <see cref="DedupeKey"/> always inserts a fresh row. <see cref="ResolvedAtUtc"/> is the single source
/// of truth for whether the alert is resolved: set when the underlying condition clears, cleared back to
/// NULL if the condition re-fires inside the same window.
/// </summary>
[DbTable("alerts")]
[DbPrimaryKey(Name = "pk_alerts", Columns = ["id"])]
[DbUniqueIndex(Name = "ux_alerts_ref", Columns = ["alert_ref"], Usage = "uniqueness")]
[DbUniqueIndex(
    Name = "ux_alerts_dedupe",
    Columns = ["namespace_id", "dedupe_key", "dedupe_window_start_utc"],
    Filter = "dedupe_key IS NOT NULL",
    Usage = "uniqueness"
)]
[DbIndex(
    Name = "ix_alerts_delivery_due",
    Columns = ["namespace_id", "delivery_status_code", "retry_after_utc", "id"],
    Usage = "maintenance"
)]
[DbIndex(
    Name = "ix_alerts_namespace_created",
    Columns = ["namespace_id", "created_at_utc", "id"],
    Descending = ["created_at_utc", "id"],
    Usage = "dashboard_grid"
)]
[DbIndex(
    Name = "ix_alerts_namespace_unresolved",
    Columns = ["namespace_id", "created_at_utc", "id"],
    Descending = ["created_at_utc", "id"],
    Filter = "resolved_at_utc IS NULL",
    Usage = "dashboard_grid"
)]
[DbCheck(Name = "ck_alerts_job_ref_pair", Sql = "(job_id IS NULL AND job_ref IS NULL) OR (job_id IS NOT NULL AND job_ref IS NOT NULL)")]
[DbCheck(
    Name = "ck_alerts_dedupe_pair",
    Sql = "(dedupe_key IS NULL AND dedupe_window_start_utc IS NULL) OR (dedupe_key IS NOT NULL AND dedupe_window_start_utc IS NOT NULL)"
)]
[DbCheck(Name = "ck_alerts_occurrence_count", Sql = "occurrence_count >= 1")]
internal sealed class JobAlert : IEntity<long>
{
    /// <summary>
    /// Alert row identifier.
    /// </summary>
    [DbColumn("id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// Public stable reference exposed to dashboards, HTTP APIs, and alert transports in place of the
    /// numeric id; rendered externally as "alr_" plus 26 lowercase Crockford Base32 characters.
    /// Allocated in C# (a UUIDv7 via <see cref="Acta.AlertRef.New"/>) and passed into the raising
    /// routine, never defaulted by the database. The upsert applies it on the INSERT arm only, so a
    /// deduplicated repeat keeps the ref its first firing minted.
    /// </summary>
    [DbColumn("alert_ref", DbKind.Guid)]
    public Guid AlertRef { get; init; }

    /// <summary>
    /// Scope of the dedupe window; alerts collapse onto one row per
    /// <c>(namespace_id, dedupe_key, dedupe_window_start_utc)</c>.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int16)]
    public short NamespaceId { get; init; }

    /// <summary>
    /// Job that triggered the alert. No FK; SP-side validation. Operators reach the definition via
    /// <c>job.definition_id</c>.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long? JobId { get; init; }

    /// <summary>
    /// Public ref of the Job that triggered the alert, denormalized at write so the alert stays publicly
    /// addressable after Job purge. NULL when <see cref="JobId"/> is null.
    /// </summary>
    [DbColumn("job_ref", DbKind.Guid)]
    public Guid? JobRef { get; init; }

    /// <summary>
    /// Origin of the alert (<c>Automatic</c> / <c>Manual</c>); shapes the
    /// deduplication-key default and audit attribution.
    /// </summary>
    [DbColumn("origin_code")]
    public AlertOriginCode OriginCode { get; init; }

    /// <summary>
    /// Severity tier (<c>Info</c> / <c>Warning</c> / <c>Error</c> / <c>Critical</c>) shaping channel routing
    /// rules and oncall pages.
    /// </summary>
    [DbColumn("severity_code")]
    public AlertSeverityCode SeverityCode { get; init; }

    /// <summary>
    /// What kind of alert this is (per the <see cref="AlertKindCode"/> taxonomy); informs triage flow.
    /// </summary>
    [DbColumn("kind_code")]
    public AlertKindCode Kind { get; init; }

    /// <summary>
    /// Short headline for delivery channels.
    /// </summary>
    [DbColumn("title", DbKind.UnicodeString, Size = 512)]
    public string Title { get; init; } = default!;

    /// <summary>
    /// Operator-readable message body; what oncall reads first. Truncated by <c>MessageTruncator</c>.
    /// </summary>
    [DbColumn("message", DbKind.UnicodeString, Size = 512)]
    public string Message { get; init; } = default!;

    /// <summary>
    /// Channel that delivers this alert; resolved at delivery time, not at write, so there is no enforced
    /// FK.
    /// </summary>
    [DbColumn("channel_name", DbKind.AsciiString, Size = 128)]
    public string ChannelName { get; init; } = default!;

    /// <summary>
    /// Operator-readable semantic grouping string (NOT a cryptographic hash). When non-null, the unique
    /// <c>(namespace_id, dedupe_key, dedupe_window_start_utc)</c> index collapses repeats inside the
    /// window onto one row; when null, every call inserts a fresh row. Sized for the Automatic-origin
    /// default template <c>auto:{definitionId}:{jobId}:{alert_kind}:{job_reason}</c> - wider than
    /// the caller-supplied <c>jobs.dedupe_key</c> (128) because Acta composes this one; the
    /// concept is the same deduplication both spell.
    /// </summary>
    [DbColumn("dedupe_key", DbKind.AsciiString, Size = 512)]
    public string? DedupeKey { get; init; }

    /// <summary>
    /// Window-bucket start aligned to the fixed one-hour dedupe window; that window IS the rate
    /// limit. NULL when <see cref="DedupeKey"/> is null (no dedupe).
    /// </summary>
    [DbColumn("dedupe_window_start_utc", DbKind.UtcInstant)]
    public DateTime? DedupeWindowStartUtc { get; init; }

    /// <summary>
    /// How many times this alert condition has fired within the dedupe window. Seeded to 1 by the
    /// alert-emitting operation on first insert (no server default); increments on repeat.
    /// </summary>
    [DbColumn("occurrence_count", DbKind.Int32)]
    public int OccurrenceCount { get; set; }

    /// <summary>
    /// High-water mark of the <c>events</c> row that last moved this alert automatically: the id of the
    /// failure event that last raised or re-opened it, or of the success event that last resolved it.
    /// NULL means no projected event has touched the row yet (a manual alert, or an automatic one whose
    /// first projection is still in flight), and sorts before every event id. The <c>sys.alerts</c>
    /// projector commits each event's alert write before it advances its cursor, so a crash mid-batch
    /// replays events already projected; every automatic transition is conditional on the incoming event
    /// id being strictly greater than this mark, which makes that replay a no-op instead of an inflated
    /// <see cref="OccurrenceCount"/> or a re-opened resolution. Manual raises and the operator resolve
    /// verb carry no event and leave this untouched.
    /// </summary>
    [DbColumn("last_projected_event_id", DbKind.Int64)]
    public long? LastProjectedEventId { get; set; }

    /// <summary>
    /// When the underlying condition cleared (recovery instant). NULL means the alert is still unresolved
    /// and is the single source of truth for resolution. Set by <c>complete_execution</c> when a
    /// previously-failed Job's next execution succeeds; cleared back to NULL when a re-failure inside the
    /// same dedupe window re-opens the row.
    /// </summary>
    [DbColumn("resolved_at_utc", DbKind.UtcInstant)]
    public DateTime? ResolvedAtUtc { get; set; }

    /// <summary>
    /// When an operator acknowledged the alert; NULL = unacknowledged. Orthogonal to
    /// <see cref="ResolvedAtUtc"/> (resolution = condition cleared; acknowledgement = operator has
    /// seen it). Who acknowledged is recorded on the events timeline, not here.
    /// </summary>
    [DbColumn("acknowledged_at_utc", DbKind.UtcInstant)]
    public DateTime? AcknowledgedAtUtc { get; internal set; }

    /// <summary>
    /// Where the alert is in the delivery pipeline (<c>Pending</c> / <c>Suppressed</c> /
    /// <c>Delivered</c> / <c>Failed</c> / <c>RetryAfter</c>).
    /// </summary>
    [DbColumn("delivery_status_code")]
    public AlertDeliveryStatusCode DeliveryStatusCode { get; set; }

    /// <summary>
    /// How many delivery attempts this row has had so far.
    /// </summary>
    [DbColumn("retry_count", DbKind.Byte)]
    public byte RetryCount { get; set; }

    /// <summary>
    /// Earliest instant the next delivery attempt may run; null when no retry is pending.
    /// </summary>
    [DbColumn("retry_after_utc", DbKind.UtcInstant)]
    public DateTime? RetryAfterUtc { get; set; }

    /// <summary>When the alert row was first created (also serves as the first-seen instant).</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the alert row was last updated. Set server-side on every mutation.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Optimistic-concurrency token; SPs manually increment via <c>SET version = version + 1</c>
    /// on every UPDATE.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
