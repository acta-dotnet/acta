using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// One named recurring schedule attached to a definition's slot Job. The <see cref="Origin"/> records
/// how the row came to exist. Sparse override model (mirrors JobDefinition): the bare field carries the
/// origin-of-truth default and its <c>*_override</c> carries the operator edit (NULL = inherit);
/// effective = <c>COALESCE(_override, default)</c>. Only <c>expression</c> and <c>time_zone_id</c> are
/// operator-overridable. The canonical per-schedule cursor is <see cref="NextRunAtUtc"/>; the slot Job's
/// cursor is the MIN over its live schedules.
/// </summary>
[DbTable("schedules")]
[DbPrimaryKey(Name = "pk_schedules", Columns = ["id"])]
[DbForeignKey(
    Name = "fk_schedules_jobs",
    Target = typeof(Job),
    TargetColumn = "id",
    Column = "job_id",
    OnDelete = DbForeignKeyAction.Cascade
)]
[DbForeignKey(
    Name = "fk_schedules_definitions",
    Target = typeof(JobDefinition),
    TargetColumn = "id",
    Column = "definition_id",
    OnDelete = DbForeignKeyAction.NoAction
)]
[DbUniqueIndex(Name = "ux_schedules_job_name", Columns = ["job_id", "name"], Usage = "uniqueness")]
[DbIndex(
    Name = "ix_schedules_namespace_next",
    Columns = ["namespace_id", "next_run_at_utc", "id"],
    Filter = "orphaned_at_utc IS NULL",
    Usage = "scheduler"
)]
internal sealed class JobSchedule : IEntity<long>
{
    /// <summary>
    /// Schedule row identifier.
    /// </summary>
    [DbColumn("id", DbKind.Int64)]
    public long Id { get; init; }

    /// <summary>
    /// Per-namespace reload tick filters by this.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int16)]
    public short NamespaceId { get; init; }

    /// <summary>
    /// Structural identity for the recurring Job row this schedule fires. The recurring Job row owns its
    /// schedules; purge cascades to all schedule rows.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; init; }

    /// <summary>
    /// Denormalized from <c>Job.DefinitionId</c> at INSERT; never mutated. Safe because
    /// <c>Job.DefinitionId</c> is <c>init</c> (immutable post-INSERT). Retained for the per-namespace
    /// reload sweep and the catalog-upsert "find Definition-origin slot for this (ns, def)" lookup without
    /// joining <c>jobs</c>.
    /// </summary>
    [DbColumn("definition_id", DbKind.Int32)]
    public int DefinitionId { get; init; }

    /// <summary>
    /// Operator-readable schedule name. For Definition-origin it matches the <c>[JobSchedule(name)]</c>
    /// attribute parameter. Kebab-case ASCII. The <c>sys.</c> prefix is reserved for system-owned
    /// schedule names, for parity with the other Name columns.
    /// </summary>
    [DbColumn("name", DbKind.AsciiString, Size = 128)]
    public string Name { get; init; } = default!;

    /// <summary>
    /// Origin of the row's existence: <c>Definition</c> (declared in a <c>[JobSchedule]</c> attribute,
    /// refreshed by catalog upsert) or <c>Operator</c>. Override columns apply to any origin.
    /// </summary>
    [DbColumn("origin_code")]
    public ScheduleOriginCode Origin { get; init; }

    // ---------- Schedule expression (paired: code/origin default, then operator _override) ----------
    // Convention matches JobDefinition: the bare-named field is the origin-of-truth default (Definition:
    // refreshed by catalog upsert; Operator/Api: set at creation); its _override is the operator edit
    // (NULL = inherit). Effective = COALESCE(_override, bare), coalesced at the read site.

    /// <summary>Cron or interval duration, human ("5m") or ISO 8601 ("PT5M") (the origin-of-truth default).</summary>
    [DbColumn("expression", DbKind.AsciiString, Size = 128)]
    public string Expression { get; set; } = default!;

    /// <summary>Operator-overridden cron or interval expression, human ("5m") or ISO 8601 ("PT5M"). NULL = no override.</summary>
    [DbColumn("expression_override", DbKind.AsciiString, Size = 128)]
    public string? ExpressionOverride { get; set; }

    /// <summary>Effective expression (DB-computed); read-only.</summary>
    [DbColumn("expression_effective", DbKind.AsciiString, Size = 128, Generated = "COALESCE(expression_override, expression)")]
    public string ExpressionEffective { get; internal set; } = default!;

    /// <summary>
    /// IANA tz id (the origin-of-truth default). "tz id" is the IANA standard term, a string-identifier
    /// exception to the integer-<c>_id</c> convention.
    /// </summary>
    [DbColumn("time_zone_id", DbKind.AsciiString, Size = 128)]
    public string TimeZoneId { get; set; } = default!;

    /// <summary>Operator-overridden IANA tz id. NULL = no override.</summary>
    [DbColumn("time_zone_id_override", DbKind.AsciiString, Size = 128)]
    public string? TimeZoneIdOverride { get; set; }

    /// <summary>Effective IANA tz id (DB-computed); read-only.</summary>
    [DbColumn("time_zone_id_effective", DbKind.AsciiString, Size = 128, Generated = "COALESCE(time_zone_id_override, time_zone_id)")]
    public string TimeZoneIdEffective { get; internal set; } = default!;

    /// <summary>
    /// Whether <see cref="Expression"/> is a cron expression or an ISO 8601 interval. Set at compile
    /// time by the source generator and refreshed by catalog upsert. No override.
    /// </summary>
    [DbColumn("expression_kind_code")]
    public ScheduleExpressionKindCode ExpressionKind { get; init; }

    /// <summary>
    /// Misfire policy when occurrences are missed during downtime (catch-up once vs. skip).
    /// </summary>
    [DbColumn("misfire_strategy_code")]
    public MisfireStrategyCode Misfire { get; init; }

    // ---------- Cursor ----------

    /// <summary>
    /// Canonical per-schedule cursor: the next instant this schedule is due. The slot Job's
    /// <c>next_run_at_utc</c> is MIN over its live schedules. NULL means the schedule is exhausted
    /// (no further occurrence). Computed in C# (<c>NextOccurrenceCalculator</c> / <c>ScheduleWalker</c>);
    /// the SQL appliers only persist it.
    /// </summary>
    [DbColumn("next_run_at_utc", DbKind.UtcInstant)]
    public DateTime? NextRunAtUtc { get; set; }

    // ---------- Flags ----------

    /// <summary>
    /// When this row was orphaned (descriptor declaration disappeared); only meaningful for
    /// <c>Origin = Definition</c>. NULL means the row is not orphaned. Set by catalog upsert when the
    /// <c>[JobSchedule]</c> declaration disappears; the walker ignores rows where this is NOT NULL.
    /// Always NULL for <c>Operator</c> and <c>Api</c> (no descriptor anchor to be orphaned from).
    /// </summary>
    [DbColumn("orphaned_at_utc", DbKind.UtcInstant)]
    public DateTime? OrphanedAtUtc { get; set; }

    // ---------- Operator lifecycle ----------

    /// <summary>
    /// Lifecycle state. <c>Active</c> schedules fire; <c>Paused</c> schedules do not and are excluded
    /// from the slot's MIN; <c>Orphaned</c> is set by catalog reconciliation alongside
    /// <see cref="OrphanedAtUtc"/> when the origin declaration disappears. Operator pause survives a
    /// redeploy; an orphaned row that is re-declared resets to <c>Active</c>.
    /// </summary>
    [DbColumn("status_code")]
    public ScheduleStatusCode Status { get; set; }

    /// <summary>
    /// When a timed pause expires. NULL means an indefinite pause (resume is operator-driven) or not
    /// paused. While set, the slot's cursor includes this instant so the scheduler wakes to auto-resume:
    /// the walker flips the row back to <c>Active</c> and reconciles the cursor by misfire policy.
    /// </summary>
    [DbColumn("paused_until_utc", DbKind.UtcInstant)]
    public DateTime? PausedUntilUtc { get; set; }

    // ---------- Description / operator note ----------

    /// <summary>
    /// Dev-authored explanation of the schedule from the [JobSchedule] attribute; NULL when the
    /// attribute sets none. Distinct from <see cref="Note"/>, which is operator-written.
    /// </summary>
    [DbColumn("description", DbKind.UnicodeString, Size = 512)]
    public string? Description { get; internal set; }

    /// <summary>
    /// Operator note explaining the change.
    /// </summary>
    [DbColumn("note", DbKind.UnicodeString, Size = 512)]
    public string? Note { get; set; }

    /// <summary>When the schedule row was created. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Last write of any kind. Drives the per-namespace reload tick.
    /// </summary>
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
