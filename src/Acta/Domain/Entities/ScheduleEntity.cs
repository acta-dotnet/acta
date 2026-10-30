namespace Acta;

/// <summary>
/// One named recurring schedule of a definition, with its own identity: its declaration and operator lifecycle.
/// <c>(definition_id, schedule_name)</c> is the natural key the path <c>namespace/job/schedule</c> resolves to, and
/// registration keeps the row's identity across deploys; a renamed schedule is a new identity. The declaration
/// (expression, time zone, kind, misfire policy, description) is code-owned and refreshed by catalog upsert; an
/// operator pauses, resumes, and triggers, never edits timing. The cursor lives on <see cref="ScheduleStateEntity"/>,
/// so a fire's plain advance never rewrites this row.
/// </summary>
[DbTable("schedules")]
[DbPrimaryKey(Name = "pk_schedules", Columns = ["schedule_id"])]
[DbUniqueIndex(Name = "ux_schedules_definition_name", Columns = ["definition_id", "schedule_name"], Usage = "uniqueness")]
// A slot's schedules: the fire plan, the verbs, and purge read every schedule of one slot job.
[DbIndex(Name = "ix_schedules_job", Columns = ["job_id", "schedule_id"], Filter = "job_id IS NOT NULL", Usage = "read_api")]
[DbForeignKey(
    Name = "fk_schedules_definitions",
    Target = typeof(DefinitionEntity),
    TargetColumn = "definition_id",
    Column = "definition_id",
    OnDelete = DbForeignKeyAction.NoAction
)]
[DbCheck(Name = "ck_schedules_paused_pair", Sql = "schedule_status_code = 30 OR paused_until_utc IS NULL")]
[DbCheck(Name = "ck_schedules_orphan_origin", Sql = "schedule_origin_code = 40 OR schedule_status_code <> 230")]
[DbCheck(Name = "ck_schedules_max_outstanding", Sql = "max_outstanding >= 1")]
[DbCheck(
    Name = "ck_schedules_expire_pair",
    Sql = "(expire_after_seconds IS NULL AND expire_action_code IS NULL) OR (expire_after_seconds IS NOT NULL AND expire_action_code IS NOT NULL)"
)]
[DbCheck(Name = "ck_schedules_expire_after", Sql = "expire_after_seconds IS NULL OR expire_after_seconds > 0")]
internal sealed class ScheduleEntity : IEntity<int>
{
    /// <summary>
    /// Primary key and the schedule's identity, database-assigned at INSERT and never reused. <c>schedules_state</c>
    /// is keyed by it. Internal: the public selector is the three-part path.
    /// </summary>
    [DbColumn("schedule_id", DbKind.Int32)]
    public int Id { get; init; }

    /// <summary>
    /// The definition's namespace, written once at insert. The startup reconcile reads a namespace's schedules by it.
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int32)]
    public int NamespaceId { get; init; }

    /// <summary>
    /// The permanent slot this schedule fires, NULL for a PerOccurrence definition. The slot owns its schedules, and
    /// purge and retention delete them with it.
    /// </summary>
    [DbColumn("job_id", DbKind.Int64)]
    public long? JobId { get; init; }

    /// <summary>
    /// The definition the schedule belongs to, written at INSERT and never mutated. With the name it is the key
    /// registration and the schedule verbs find the row by, slot or no slot.
    /// </summary>
    [DbColumn("definition_id", DbKind.Int32)]
    public int DefinitionId { get; init; }

    /// <summary>
    /// Operator-readable schedule name, unique within its definition. For Definition-origin it matches the
    /// <c>[Schedule(name)]</c> attribute parameter. Kebab-case ASCII. The <c>sys.</c> prefix is reserved for
    /// system-owned schedule names, for parity with the other Name columns.
    /// </summary>
    [DbColumn("schedule_name", DbKind.AsciiString, Size = 128)]
    public string ScheduleName { get; init; } = default!;

    /// <summary>
    /// Origin of the row's existence: <c>Definition</c> (declared in a <c>[Schedule]</c> attribute,
    /// refreshed by catalog upsert) or <c>Operator</c>.
    /// </summary>
    [DbColumn("schedule_origin_code")]
    public ScheduleOriginCode Origin { get; init; }

    // ---------- Schedule expression (code-owned, refreshed by catalog upsert) ----------

    /// <summary>
    /// Cron or interval duration, human ("5m") or ISO 8601 ("PT5M"); <c>expression</c> holds the calendar's name when the
    /// kind is Calendar.
    /// </summary>
    [DbColumn("expression", DbKind.AsciiString, Size = 128)]
    public string Expression { get; set; } = default!;

    /// <summary>
    /// IANA tz id. "tz id" is the IANA standard term, a string-identifier exception to the integer-<c>_id</c>
    /// convention.
    /// </summary>
    [DbColumn("time_zone_id", DbKind.AsciiString, Size = 128)]
    public string TimeZoneId { get; set; } = default!;

    /// <summary>
    /// Whether <see cref="Expression"/> is a cron expression, an ISO 8601 interval, or a calendar's name. Set at compile
    /// time by the source generator and refreshed by catalog upsert.
    /// </summary>
    [DbColumn("expression_kind_code")]
    public ScheduleExpressionKindCode ExpressionKind { get; init; }

    /// <summary>
    /// MisfireStrategy policy when occurrences are missed during downtime (catch-up once vs. skip).
    /// </summary>
    [DbColumn("misfire_strategy_code")]
    public MisfireStrategyCode MisfireStrategy { get; init; }

    /// <summary>
    /// The instant an interval's ticks step from, written at the schedule's first registration and kept across
    /// deploys, so a late evaluation never shifts the sequence. NULL for a cron, which resolves in its zone.
    /// </summary>
    [DbColumn("interval_anchor_at_utc", DbKind.UtcInstant)]
    public DateTime? IntervalAnchorAtUtc { get; init; }

    /// <summary>
    /// How long past its instant a due occurrence still counts as on time rather than missed: the misfire policy
    /// decides only what is older. Covers a slow admission pass, not downtime.
    /// </summary>
    [DbColumn("misfire_grace_seconds", DbKind.Int32)]
    public int MisfireGraceSeconds { get; init; }

    /// <summary>
    /// <see cref="TimingFingerprint"/> of the declaration as registered: the expression, its kind, the zone and its
    /// rules, the anchor, and the availability calendar's name. A registration whose fingerprint matches leaves the cursor alone; one that differs is a
    /// timing change.
    /// </summary>
    [DbColumn("timing_fingerprint", DbKind.AsciiString, Size = 64)]
    public string TimingFingerprint { get; init; } = default!;

    /// <summary>
    /// How many admitted occurrences may be unsettled at once, from <c>[Schedule(MaxOutstanding)]</c>; a Permanent
    /// definition's schedules hold 1, the slot.
    /// </summary>
    [DbColumn("max_outstanding", DbKind.Int32)]
    public int MaxOutstanding { get; init; }

    /// <summary>What a due occurrence does while the schedule is at <see cref="MaxOutstanding"/>: wait or drop.</summary>
    [DbColumn("full_behavior_code")]
    public ScheduleFullBehaviorCode FullBehavior { get; init; }

    /// <summary>
    /// The age past its instant an occurrence may reach before it expires unadmitted, from
    /// <c>[Schedule(ExpireAfter)]</c>; NULL means it never expires. Set together with <see cref="ExpireAction"/>.
    /// </summary>
    [DbColumn("expire_after_seconds", DbKind.Int32)]
    public int? ExpireAfterSeconds { get; init; }

    /// <summary>What an occurrence older than <see cref="ExpireAfterSeconds"/> does when evaluated: discard or pause.</summary>
    [DbColumn("expire_action_code")]
    public ScheduleExpireActionCode? ExpireAction { get; init; }

    /// <summary>
    /// The name of the availability calendar the schedule observes, from <c>[Schedule(Availability)]</c>, whatever the
    /// expression kind; NULL means none. Like a calendar's name in <see cref="Expression"/>, it is all the row keeps of
    /// the binding: the name resolves afresh at every evaluation.
    /// </summary>
    [DbColumn("availability_calendar_name", DbKind.AsciiString, Size = 128)]
    public string? AvailabilityCalendarName { get; init; }

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
    /// Dev-authored explanation of the schedule from the [Schedule] attribute; NULL when the
    /// attribute sets none. Distinct from <see cref="PauseMessage"/>, which is operator-written.
    /// </summary>
    [DbColumn("description", DbKind.UnicodeString, Size = 512)]
    public string? Description { get; internal set; }

    /// <summary>
    /// The operator's reason for the current pause, the same reasonMessage text the audit event records, kept on
    /// the row so a paused schedule explains itself. NULL whenever the row is not Paused: resume, an elapsed timed
    /// pause, and orphaning clear it; events keep the history.
    /// </summary>
    [DbColumn("pause_message", DbKind.UnicodeString, Size = 512)]
    public string? PauseMessage { get; set; }

    /// <summary>When the schedule row was created. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When this row was last written; a plain advance writes only the cursor row.</summary>
    [DbColumn("modified_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>
    /// Optimistic-concurrency token, bumped by every write to this row: a pause or resume, a registration that changes
    /// the declaration or moves the cursor, and the fire that ends an elapsed timed pause. A plain advance writes only
    /// the cursor row. A recurring completion advances a schedule only at the version its plan read, so an edit or
    /// registration that landed while the attempt ran wins.
    /// </summary>
    [DbColumn("version", DbKind.Int32, Default = DbDefault.Zero)]
    [DbConcurrencyToken]
    public int Version { get; set; }
}
