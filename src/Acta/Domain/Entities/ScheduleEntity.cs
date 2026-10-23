namespace Acta;

/// <summary>
/// One named recurring schedule of a definition's slot Job, keyed by that job and its name: its declaration and
/// operator lifecycle. Sparse override model (mirrors <c>definitions</c>): the bare field carries the origin-of-truth
/// default and its <c>*_override</c> the operator edit (NULL = inherit); effective = <c>COALESCE(_override, default)</c>.
/// Only <c>expression</c> and <c>time_zone_id</c> are operator-overridable. The cursor lives on
/// <see cref="ScheduleStateEntity"/>, so a fire's plain advance never rewrites this row.
/// </summary>
[DbTable("schedules")]
[DbPrimaryKey(Name = "pk_schedules", Columns = ["job_id", "schedule_name"])]
[DbForeignKey(
    Name = "fk_schedules_definitions",
    Target = typeof(DefinitionEntity),
    TargetColumn = "definition_id",
    Column = "definition_id",
    OnDelete = DbForeignKeyAction.NoAction
)]
[DbCheck(Name = "ck_schedules_paused_pair", Sql = "schedule_status_code = 30 OR paused_until_utc IS NULL")]
[DbCheck(Name = "ck_schedules_orphan_origin", Sql = "schedule_origin_code = 40 OR schedule_status_code <> 230")]
internal sealed class ScheduleEntity : IEntity
{
    /// <summary>
    /// The owning slot Job's namespace, written once at insert. The startup reconcile reads a namespace's schedules by it.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// Structural identity for the recurring Job row this schedule fires. The recurring Job row owns its
    /// schedules, and purge and retention delete them with it. The key's first column.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long JobId { get; init; }

    /// <summary>
    /// Denormalized from <c>jobs.definition_id</c> at INSERT and never mutated, since a job's definition never
    /// changes. Retained for the per-namespace reconcile read and the catalog-upsert "find Definition-origin slot for
    /// this (ns, def)" lookup without joining <c>jobs</c>.
    /// </summary>
    [DbColumn("definition_id", DbKind.Int32)]
    public int DefinitionId { get; init; }

    /// <summary>
    /// Operator-readable schedule name. For Definition-origin it matches the <c>[JobSchedule(name)]</c>
    /// attribute parameter. Kebab-case ASCII. The <c>sys.</c> prefix is reserved for system-owned
    /// schedule names, for parity with the other Name columns. The key's second column.
    /// </summary>
    [DbColumn("schedule_name", DbKind.AsciiString, Size = 128)]
    public string ScheduleName { get; init; } = default!;

    /// <summary>
    /// Origin of the row's existence: <c>Definition</c> (declared in a <c>[JobSchedule]</c> attribute,
    /// refreshed by catalog upsert) or <c>Operator</c>. Override columns apply to any origin.
    /// </summary>
    [DbColumn("schedule_origin_code")]
    public ScheduleOriginCode Origin { get; init; }

    // ---------- Schedule expression (paired: code/origin default, then operator _override) ----------
    // Convention matches DefinitionEntity: the bare-named field is the origin-of-truth default (Definition:
    // refreshed by catalog upsert; Operator: set at creation); its _override is the operator edit
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
    /// MisfireStrategy policy when occurrences are missed during downtime (catch-up once vs. skip).
    /// </summary>
    [DbColumn("misfire_strategy_code")]
    public MisfireStrategyCode MisfireStrategy { get; init; }

    // ---------- Operator lifecycle ----------

    /// <summary>
    /// Lifecycle state. <c>Active</c> schedules fire; <c>Paused</c> ones do not and leave the slot's MIN;
    /// <c>Orphaned</c> marks a <c>Definition</c>-origin row whose declaration disappeared, which the walker ignores. A
    /// re-declared orphan resets to <c>Active</c>, dropping any pause: a schedule that came back warrants a fresh
    /// operator decision.
    /// </summary>
    [DbColumn("schedule_status_code")]
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
    /// attribute sets none. Distinct from <see cref="ChangeMessage"/>, which is operator-written.
    /// </summary>
    [DbColumn("description", DbKind.UnicodeString, Size = 512)]
    public string? Description { get; internal set; }

    /// <summary>
    /// Operator-authored justification for the last pause/resume/overrides change: the same
    /// reasonMessage text the audit event records, kept on the row so the current state explains
    /// itself.
    /// </summary>
    [DbColumn("change_message", DbKind.UnicodeString, Size = 512)]
    public string? ChangeMessage { get; set; }

    /// <summary>When the schedule row was created. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When this row was last written; a plain advance writes only the cursor row.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Optimistic-concurrency token, bumped by every write to this row: an operator edit, a registration that changes
    /// the declaration or moves the cursor, and the fire that ends an elapsed timed pause. A plain advance writes only
    /// the cursor row. A recurring completion advances a schedule only at the version its plan read, so an edit or
    /// registration that landed while the attempt ran wins.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
