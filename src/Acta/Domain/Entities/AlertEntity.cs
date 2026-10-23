namespace Acta;

/// <summary>
/// One materialized alert. A non-null <see cref="DeduplicationKey"/> makes it an incident: the unique
/// <c>(namespace_id, deduplication_key)</c> index over unresolved rows admits one open row per key, so repeats collapse onto
/// it (<see cref="OccurrenceCount"/>) while it stays open, and a null key always inserts. <see cref="ResolvedAtUtc"/>
/// is terminal: once stamped it never clears, and the next firing opens a new incident on the same key.
/// </summary>
[DbTable("alerts")]
[DbPrimaryKey(Name = "pk_alerts", Columns = ["alert_id"])]
[DbUniqueIndex(Name = "ux_alerts_ref", Columns = ["alert_ref"], Usage = "uniqueness")]
[DbUniqueIndex(
    Name = "ux_alerts_deduplication_key",
    Columns = ["namespace_id", "deduplication_key"],
    Filter = "deduplication_key IS NOT NULL AND resolved_at_utc IS NULL",
    Usage = "uniqueness"
)]
// ux_alerts_deduplication_key covers only the OPEN row of an identity, but the raise also has to see the closed
// ones: its ghost guard asks whether ANY row of the identity already carries a mark at or past the
// incoming event, and its held-write fallback reads the identity's newest row whatever its resolution.
// Neither can use the resolved_at_utc arm of that filter, so without this they degrade to a
// namespace-wide scan on every raise that opens an incident or is blocked from opening one. The
// deduplication_key arm is kept: an equality on the column implies it, so all three planners still seek, and
// keyless rows - which no raise ever looks up this way - stay out. Two columns is enough: one identity
// holds a handful of incidents, so the ORDER BY alert_id the fallback adds sorts almost nothing.
[DbIndex(
    Name = "ix_alerts_deduplication_key_identity",
    Columns = ["namespace_id", "deduplication_key"],
    Filter = "deduplication_key IS NOT NULL",
    Usage = "alert_raise"
)]
// Serves the whole delivery read, not just the retry arm: both arms filter on the namespace, then a
// delivery status, then retry_after_utc, so the reminder arm seeks the same three columns in the same
// order and the ORDER BY alert_id rides the trailing key.
[DbIndex(
    Name = "ix_alerts_delivery_due",
    Columns = ["namespace_id", "delivery_status_code", "retry_after_utc", "alert_id"],
    Usage = "maintenance"
)]
[DbIndex(
    Name = "ix_alerts_namespace_created",
    Columns = ["namespace_id", "created_at_utc", "alert_id"],
    Descending = ["created_at_utc", "alert_id"],
    Usage = "dashboard_grid"
)]
[DbIndex(
    Name = "ix_alerts_namespace_unresolved",
    Columns = ["namespace_id", "created_at_utc", "alert_id"],
    Descending = ["created_at_utc", "alert_id"],
    Filter = "resolved_at_utc IS NULL",
    Usage = "dashboard_grid"
)]
// A job's own alerts: purge and resolve lock that set after the job row
// (docs/sql-internals.md, "Alert lock order"), and the job-filtered alert list reads it.
// Namespace-level alerts carry no job and stay out.
[DbIndex(Name = "ix_alerts_job", Columns = ["job_id", "alert_id"], Filter = "job_id IS NOT NULL", Usage = "maintenance")]
[DbCheck(Name = "ck_alerts_job_ref_pair", Sql = "(job_id IS NULL AND job_ref IS NULL) OR (job_id IS NOT NULL AND job_ref IS NOT NULL)")]
[DbCheck(Name = "ck_alerts_occurrence_count", Sql = "occurrence_count >= 1")]
internal sealed class AlertEntity : IEntity<long>
{
    /// <summary>
    /// Alert row identifier.
    /// </summary>
    [DbColumn("alert_id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// Public stable reference exposed to dashboards, HTTP APIs, and alert transports in place of the
    /// numeric id; rendered externally as "alr_" plus the Guid's 32 lowercase hex digits.
    /// Allocated in C# (a UUIDv7 via <see cref="Acta.AlertRef.New"/>) and passed into the raising
    /// routine, never defaulted by the database. The upsert applies it on the INSERT arm only, so every
    /// repeat absorbed by an open incident keeps the ref that incident's first firing minted, and the
    /// next incident on the same key gets a ref of its own.
    /// </summary>
    [DbColumn("alert_ref", DbKind.Guid)]
    public Guid AlertRef { get; init; }

    /// <summary>
    /// Scope of the incident identity; at most one unresolved alert exists per
    /// <c>(namespace_id, deduplication_key)</c>.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// Job that triggered the alert. No FK; SP-side validation. Operators reach the definition via
    /// <c>jobs.definition_id</c>.
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
    [DbColumn("alert_origin_code")]
    public AlertOriginCode OriginCode { get; init; }

    /// <summary>
    /// Severity tier (<c>Info</c> / <c>Warning</c> / <c>Error</c> / <c>Critical</c>) shaping channel routing
    /// rules and oncall pages.
    /// </summary>
    [DbColumn("alert_severity_code")]
    public AlertSeverityCode SeverityCode { get; init; }

    /// <summary>
    /// What kind of alert this is (per the <see cref="AlertKindCode"/> taxonomy); informs triage flow.
    /// </summary>
    [DbColumn("alert_kind_code")]
    public AlertKindCode Kind { get; init; }

    /// <summary>
    /// Short headline for delivery channels.
    /// </summary>
    [DbColumn("title", DbKind.UnicodeString, Size = 512)]
    public string Title { get; init; } = default!;

    /// <summary>
    /// Operator-readable message body; what oncall reads first. Truncated by <c>MessageTruncator</c>.
    /// </summary>
    [DbColumn("alert_message", DbKind.UnicodeString, Size = 512)]
    public string AlertMessage { get; init; } = default!;

    /// <summary>
    /// Channel that delivers this alert; resolved at delivery time, not at write, so there is no enforced
    /// FK.
    /// </summary>
    [DbColumn("channel_name", DbKind.AsciiString, Size = 128)]
    public string ChannelName { get; init; } = default!;

    /// <summary>
    /// Operator-readable semantic grouping string (NOT a cryptographic hash). When non-null it names the
    /// incident identity: the unique <c>(namespace_id, deduplication_key)</c> index over unresolved rows
    /// collapses repeats onto the one open row; when null, every call inserts a fresh row. As wide as every key,
    /// <c>jobs.deduplication_key</c> included: the Automatic-origin default template
    /// <c>auto:{definitionId}:{jobId}:{alert_kind}:{job_reason}</c> stays under 100 characters.
    /// </summary>
    [DbColumn("deduplication_key", DbKind.AsciiString, Size = 128, CaseExact = true)]
    public string? DeduplicationKey { get; init; }

    /// <summary>
    /// How many times this alert condition has fired within THIS incident - since the row opened, not
    /// over the job's life. Seeded to 1 by the alert-emitting operation on first insert (no server
    /// default); increments on every repeat the open row absorbs, and never decreases. The next incident
    /// on the same key starts a new row back at 1.
    /// </summary>
    [DbColumn("occurrence_count", DbKind.Int32)]
    public int OccurrenceCount { get; set; }

    /// <summary>
    /// The id of the <c>events</c> row that last moved this alert automatically (the failure that raised it or the
    /// success that resolved it); null until a projected event touches it, sorting before every id. Every automatic
    /// transition requires a newer event id, so a projector crash that replays a batch changes nothing, and the mark
    /// outlives resolution so a replayed failure cannot reopen a closed incident. Manual raises and resolves leave it.
    /// </summary>
    [DbColumn("last_projected_event_id", DbKind.Int64)]
    public long? LastProjectedEventId { get; set; }

    /// <summary>
    /// When the underlying condition cleared (recovery instant). NULL means the alert is still unresolved
    /// and is the single source of truth for resolution. Set when a previously-failed Job's next
    /// execution succeeds, or by the operator resolve verb. Terminal for the row: no raise ever clears it
    /// back to NULL - the next failure on the same <see cref="DeduplicationKey"/> opens a new incident row
    /// instead, which is also what frees the filtered unique index for that key again.
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
    /// Attempts spent in the current send series, which is the delivery retry budget and not a lifetime
    /// count: a delivered send ends its series and resets this to 0, so a reminder that re-notifies a
    /// still-open incident starts with the whole retry curve rather than whatever the last series
    /// happened to spend. A row sitting at the cap in <c>Failed</c> is a series that ran out.
    /// </summary>
    [DbColumn("retry_count", DbKind.Int32)]
    public int RetryCount { get; set; }

    /// <summary>
    /// Earliest instant this row may be sent again: the next retry's backoff while delivery is unsettled, and one
    /// <c>AlertReminderInterval</c> out once a send settles an unresolved row, which is how an open incident keeps
    /// reminding. Not derived from <see cref="ModifiedAtUtc"/>, which every absorbed repeat re-stamps. Null means
    /// nothing is scheduled: a delivered manual alert, a suppressed row, or any resolved row.
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
