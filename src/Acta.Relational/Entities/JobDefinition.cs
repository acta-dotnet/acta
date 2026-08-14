using Acta.Relational.Schema;

namespace Acta.Relational.Entities;

/// <summary>
/// The live job policy: one row per definition, the single source of truth for every per-job policy.
/// Each policy field is a <b>default</b> (code-owned, synced from <c>[Job]</c>) paired with a nullable
/// <b>override</b> (operator-edited, NULL = none); the <b>effective</b> value is
/// <c>COALESCE(&lt;field&gt;_override, &lt;field&gt;)</c>, computed at the point of use and never stored.
/// Identity, the type contract, and formats are code-fixed (no override). One entity, never split.
/// </summary>
[DbTable("definitions")]
[DbPrimaryKey(Name = "pk_definitions", Columns = ["id"])]
[DbUniqueIndex(Name = "ux_definitions_namespace_name", Columns = ["namespace_id", "name"], Usage = "uniqueness")]
[DbCheck(Name = "ck_definitions_max_attempts", Sql = "max_attempts >= 1")]
[DbCheck(Name = "ck_definitions_max_attempts_override", Sql = "max_attempts_override IS NULL OR max_attempts_override >= 1")]
[DbCheck(Name = "ck_definitions_execution_timeout", Sql = "execution_timeout_seconds > 0")]
[DbCheck(
    Name = "ck_definitions_execution_timeout_override",
    Sql = "execution_timeout_seconds_override IS NULL OR execution_timeout_seconds_override > 0"
)]
[DbCheck(Name = "ck_definitions_deadline", Sql = "deadline_seconds >= 0")]
[DbCheck(Name = "ck_definitions_deadline_override", Sql = "deadline_seconds_override IS NULL OR deadline_seconds_override >= 0")]
[DbCheck(Name = "ck_definitions_retention", Sql = "retention_seconds >= 0")]
[DbCheck(Name = "ck_definitions_retention_override", Sql = "retention_seconds_override IS NULL OR retention_seconds_override >= 0")]
internal sealed class JobDefinition : IEntity<int>
{
    // ---------- FRONT: identity + control (stable, read first) ----------

    /// <summary>
    /// Surrogate PK referenced by <c>Job.DefinitionId</c>, <c>JobEvent.DefinitionId</c>, and
    /// <c>JobSchedule.DefinitionId</c>.
    /// </summary>
    [DbColumn("id", DbKind.Int32)]
    public int Id { get; init; }

    /// <summary>
    /// Owning service's namespace; logical FK to <c>JobNamespace.Id</c> (no enforced FK, SP-side
    /// validation).
    /// </summary>
    [DbColumn("namespace_id", DbKind.Int16)]
    public short NamespaceId { get; init; }

    /// <summary>
    /// Operator-facing kebab-case identifier; the natural key together with the namespace.
    /// </summary>
    [DbColumn("name", DbKind.AsciiString, Size = 128)]
    public string Name { get; init; } = default!;

    /// <summary>
    /// Lifecycle of this definition (<c>Active</c> / <c>Retired</c>), code/sync-owned; enqueue is
    /// rejected at the DB boundary by <c>enqueue_batch</c> when not <c>Active</c>. No override.
    /// </summary>
    [DbColumn("status_code")]
    public JobDefinitionStatusCode Status { get; internal set; }

    /// <summary>
    /// SHA-256 hex over all code-owned columns (policy defaults + contract + formats), computed C#-side
    /// at registration. Drives the write short-circuit: the C# gate compares this against the stored
    /// value and only sends rows whose hash differs (or that are new / not Active) into the upsert.
    /// Operator override columns are NOT part of this hash, so overrides survive re-sync. Identity-adjacent
    /// control field: which code-version wrote this row.
    /// </summary>
    [DbColumn("definition_hash", DbKind.AsciiString, Size = 128)]
    public string DefinitionHash { get; internal set; } = default!;

    /// <summary>
    /// Manifest generation (build timestamp) of the worker that last wrote this row. The monotonic
    /// governor: a registration may update the row only when its incoming generation is greater than
    /// or equal to this value, so an older deployment cannot roll policy back.
    /// </summary>
    [DbColumn("manifest_generation_at_utc", DbKind.UtcInstant)]
    public DateTime ManifestGenerationAtUtc { get; internal set; }

    // ---------- Code-fixed type contract + formats (synced from [Job]; NO override) ----------

    /// <summary>
    /// CLR full type name of the <c>[Job]</c> handler's input contract; the type-driven dispatch key for
    /// <c>IJobs.EnqueueAsync&lt;TIn&gt;</c>.
    /// </summary>
    [DbColumn("input_type_name", DbKind.AsciiString, Size = 512)]
    public string InputTypeName { get; internal set; } = default!;

    /// <summary>
    /// Default payload format id for input payloads enqueued against this definition; stamped into
    /// <c>Job.InputFormatId</c> at enqueue.
    /// </summary>
    [DbColumn("input_format_id", DbKind.Byte)]
    public byte InputFormatId { get; internal set; }

    /// <summary>
    /// Default input payload format name, from the handler's <c>[Job(Format = "...")]</c> or
    /// <c>[Job(InputFormat = "...")]</c> declaration; resolved against registered serializers at startup.
    /// </summary>
    [DbColumn("input_format_name", DbKind.AsciiString, Size = 128)]
    public string InputFormatName { get; internal set; } = default!;

    /// <summary>
    /// CLR full type name of the handler's output contract; null for void handlers.
    /// </summary>
    [DbColumn("output_type_name", DbKind.AsciiString, Size = 512)]
    public string? OutputTypeName { get; internal set; }

    /// <summary>
    /// Default payload format id for execution results; stamped into <c>JobResult.ResultFormatId</c>
    /// on terminal success.
    /// </summary>
    [DbColumn("output_format_id", DbKind.Byte)]
    public byte OutputFormatId { get; internal set; }

    /// <summary>
    /// Default payload format name for execution results; mirrors <see cref="InputFormatName"/>.
    /// </summary>
    [DbColumn("output_format_name", DbKind.AsciiString, Size = 128)]
    public string OutputFormatName { get; internal set; } = default!;

    /// <summary>
    /// Whether jobs of this definition must, may, or must not carry a tenant; enforced by the enqueue
    /// routines. Code-fixed like the type contract (no operator override triple): the requirement is
    /// contract-adjacent, and an operator flip would break handler assumptions about tenant scope.
    /// </summary>
    [DbColumn("tenant_requirement_code")]
    public JobTenantRequirementCode TenantRequirement { get; internal set; }

    // ---------- Policy (triples: default, operator _override, DB-computed _effective) ----------
    // Convention: each code-owned default is bare-named; its operator override is the same name +
    // "_override" (nullable, NULL = inherit); its effective value is a STORED generated column
    // "_effective" = COALESCE(override, default), materialized by the DB and read-only here. Read sites
    // select the _effective column. To add a policy field, insert its default + _override + _effective
    // triple here, in the same position.

    /// <summary>
    /// Default priority for Jobs enqueued from this definition; per-instance overrides apply at
    /// enqueue and post-enqueue.
    /// </summary>
    [DbColumn("priority_code")]
    public JobPriorityCode Priority { get; internal set; }

    /// <summary>Operator override of <see cref="Priority"/>; NULL = inherit the default.</summary>
    [DbColumn("priority_code_override")]
    public JobPriorityCode? PriorityOverride { get; internal set; }

    /// <summary>Effective priority (DB-computed); read-only.</summary>
    [DbColumn("priority_code_effective", Generated = "COALESCE(priority_code_override, priority_code)")]
    public JobPriorityCode PriorityEffective { get; internal set; }

    /// <summary>
    /// Cap on consecutive failures before terminal <c>Status = Failed</c> for one-off jobs; resets on
    /// success. Recurring slots never terminalize on the count (MaxAttempts is the one-off budget only).
    /// </summary>
    [DbColumn("max_attempts", DbKind.Int16)]
    public short MaxAttempts { get; internal set; }

    /// <summary>Operator override of <see cref="MaxAttempts"/>; NULL = inherit the default.</summary>
    [DbColumn("max_attempts_override", DbKind.Int16)]
    public short? MaxAttemptsOverride { get; internal set; }

    /// <summary>Effective max-attempts (DB-computed); read-only.</summary>
    [DbColumn("max_attempts_effective", DbKind.Int16, Generated = "COALESCE(max_attempts_override, max_attempts)")]
    public short MaxAttemptsEffective { get; internal set; }

    /// <summary>
    /// Retry backoff policy as an Acta backoff expression, e.g. <c>"1m..8h x2 ~10%"</c>. Resolved to a
    /// concrete expression at registration (framework default <c>"1m..1d x2 ~10%"</c> when the attribute sets
    /// none); parsed by workers, never by SQL.
    /// </summary>
    [DbColumn("backoff", DbKind.UnicodeString, Size = 64)]
    public string Backoff { get; internal set; } = default!;

    /// <summary>Operator override of <see cref="Backoff"/>; NULL = inherit. Validated as a parseable expression at write.</summary>
    [DbColumn("backoff_override", DbKind.UnicodeString, Size = 64)]
    public string? BackoffOverride { get; internal set; }

    /// <summary>Effective backoff expression (DB-computed); read-only.</summary>
    [DbColumn("backoff_effective", DbKind.UnicodeString, Size = 64, Generated = "COALESCE(backoff_override, backoff)")]
    public string BackoffEffective { get; internal set; } = default!;

    /// <summary>
    /// Per-attempt wall-clock cap; cancels the handler's <c>CancellationToken</c> when exceeded.
    /// Does not span retries. Capped so a duration always fits <c>JobEvent.DurationMs</c>.
    /// </summary>
    [DbColumn("execution_timeout_seconds", DbKind.Int32)]
    public int ExecutionTimeoutSeconds { get; internal set; }

    /// <summary>Operator override of <see cref="ExecutionTimeoutSeconds"/>; NULL = inherit.</summary>
    [DbColumn("execution_timeout_seconds_override", DbKind.Int32)]
    public int? ExecutionTimeoutSecondsOverride { get; internal set; }

    /// <summary>Effective execution timeout (DB-computed); read-only.</summary>
    [DbColumn(
        "execution_timeout_seconds_effective",
        DbKind.Int32,
        Generated = "COALESCE(execution_timeout_seconds_override, execution_timeout_seconds)"
    )]
    public int ExecutionTimeoutSecondsEffective { get; internal set; }

    /// <summary>
    /// Whole-job deadline in seconds from creation. 0 means no deadline.
    /// </summary>
    [DbColumn("deadline_seconds", DbKind.Int32)]
    public int DeadlineSeconds { get; internal set; }

    /// <summary>Operator override of <see cref="DeadlineSeconds"/>; NULL = inherit.</summary>
    [DbColumn("deadline_seconds_override", DbKind.Int32)]
    public int? DeadlineSecondsOverride { get; internal set; }

    /// <summary>Effective deadline (DB-computed); read-only.</summary>
    [DbColumn("deadline_seconds_effective", DbKind.Int32, Generated = "COALESCE(deadline_seconds_override, deadline_seconds)")]
    public int DeadlineSecondsEffective { get; internal set; }

    /// <summary>
    /// How the engine treats a job past its deadline.
    /// </summary>
    [DbColumn("deadline_behavior_code")]
    public DeadlineBehaviorCode DeadlineBehavior { get; internal set; }

    /// <summary>Operator override of <see cref="DeadlineBehavior"/>; NULL = inherit.</summary>
    [DbColumn("deadline_behavior_code_override")]
    public DeadlineBehaviorCode? DeadlineBehaviorOverride { get; internal set; }

    /// <summary>Effective deadline behavior (DB-computed); read-only.</summary>
    [DbColumn("deadline_behavior_code_effective", Generated = "COALESCE(deadline_behavior_code_override, deadline_behavior_code)")]
    public DeadlineBehaviorCode DeadlineBehaviorEffective { get; internal set; }

    /// <summary>
    /// How long terminal <c>Job</c> rows are kept before <c>sys.retention</c> deletes them.
    /// </summary>
    [DbColumn("retention_seconds", DbKind.Int32)]
    public int JobRetentionSeconds { get; internal set; }

    /// <summary>Operator override of <see cref="JobRetentionSeconds"/>; NULL = inherit.</summary>
    [DbColumn("retention_seconds_override", DbKind.Int32)]
    public int? JobRetentionSecondsOverride { get; internal set; }

    /// <summary>Effective retention (DB-computed); read-only.</summary>
    [DbColumn("retention_seconds_effective", DbKind.Int32, Generated = "COALESCE(retention_seconds_override, retention_seconds)")]
    public int JobRetentionSecondsEffective { get; internal set; }

    /// <summary>
    /// Controls <c>JobEvent</c> emission (<c>Off</c> / <c>Failures</c> / <c>Audit</c>) on hot-path state
    /// mutations. Does not gate alert MERGE; alerts and audit are independent.
    /// </summary>
    [DbColumn("audit_level_code")]
    public JobAuditLevelCode AuditLevel { get; internal set; }

    /// <summary>Operator override of <see cref="AuditLevel"/>; NULL = inherit.</summary>
    [DbColumn("audit_level_code_override")]
    public JobAuditLevelCode? AuditLevelOverride { get; internal set; }

    /// <summary>Effective audit level (DB-computed); read-only.</summary>
    [DbColumn("audit_level_code_effective", Generated = "COALESCE(audit_level_code_override, audit_level_code)")]
    public JobAuditLevelCode AuditLevelEffective { get; internal set; }

    /// <summary>
    /// Automatic-alert behavior for state-mutating operations (<c>None</c> / <c>OnFailure</c> /
    /// <c>Info</c> / <c>OnTerminal</c> / <c>SysCritical</c>).
    /// </summary>
    [DbColumn("alert_profile_code")]
    public AlertProfileCode AlertProfile { get; internal set; }

    /// <summary>Operator override of <see cref="AlertProfile"/>; NULL = inherit.</summary>
    [DbColumn("alert_profile_code_override")]
    public AlertProfileCode? AlertProfileOverride { get; internal set; }

    /// <summary>Effective alert profile (DB-computed); read-only.</summary>
    [DbColumn("alert_profile_code_effective", Generated = "COALESCE(alert_profile_code_override, alert_profile_code)")]
    public AlertProfileCode AlertProfileEffective { get; internal set; }

    /// <summary>
    /// Channel that automatic alerts route to; must resolve to a startup-configured alert channel at
    /// delivery / config-validation time. The database stores only this logical routing name.
    /// </summary>
    [DbColumn("alert_channel_name", DbKind.AsciiString, Size = 128)]
    public string? AlertChannelName { get; internal set; }

    /// <summary>Operator override of <see cref="AlertChannelName"/>; NULL = inherit.</summary>
    [DbColumn("alert_channel_name_override", DbKind.AsciiString, Size = 128)]
    public string? AlertChannelNameOverride { get; internal set; }

    /// <summary>Effective alert channel (DB-computed); read-only. Nullable when neither is set.</summary>
    [DbColumn(
        "alert_channel_name_effective",
        DbKind.AsciiString,
        Size = 128,
        Generated = "COALESCE(alert_channel_name_override, alert_channel_name)"
    )]
    public string? AlertChannelNameEffective { get; internal set; }

    /// <summary>
    /// Runbook URL surfaced on alerts and the operator dashboard.
    /// </summary>
    [DbColumn("runbook_url", DbKind.AsciiString, Size = 512)]
    public string? RunbookUrl { get; internal set; }

    /// <summary>Operator override of <see cref="RunbookUrl"/>; NULL = inherit.</summary>
    [DbColumn("runbook_url_override", DbKind.AsciiString, Size = 512)]
    public string? RunbookUrlOverride { get; internal set; }

    /// <summary>Effective runbook URL (DB-computed); read-only. Nullable when neither is set.</summary>
    [DbColumn("runbook_url_effective", DbKind.AsciiString, Size = 512, Generated = "COALESCE(runbook_url_override, runbook_url)")]
    public string? RunbookUrlEffective { get; internal set; }

    /// <summary>Human display label from the [Job] attribute; NULL when the attribute sets none.</summary>
    [DbColumn("display_name", DbKind.UnicodeString, Size = 128)]
    public string? DisplayName { get; internal set; }

    /// <summary>Operator override of <see cref="DisplayName"/>; NULL = inherit.</summary>
    [DbColumn("display_name_override", DbKind.UnicodeString, Size = 128)]
    public string? DisplayNameOverride { get; internal set; }

    /// <summary>Effective display label (DB-computed); read-only. Nullable when neither is set.</summary>
    [DbColumn("display_name_effective", DbKind.UnicodeString, Size = 128, Generated = "COALESCE(display_name_override, display_name)")]
    public string? DisplayNameEffective { get; internal set; }

    /// <summary>Operator-facing description from the [Job] attribute; NULL when the attribute sets none.</summary>
    [DbColumn("description", DbKind.UnicodeString, Size = 512)]
    public string? Description { get; internal set; }

    /// <summary>Operator override of <see cref="Description"/>; NULL = inherit.</summary>
    [DbColumn("description_override", DbKind.UnicodeString, Size = 512)]
    public string? DescriptionOverride { get; internal set; }

    /// <summary>Effective description (DB-computed); read-only. Nullable when neither is set.</summary>
    [DbColumn("description_effective", DbKind.UnicodeString, Size = 512, Generated = "COALESCE(description_override, description)")]
    public string? DescriptionEffective { get; internal set; }

    // ---------- TAIL: audit bookkeeping (boilerplate, rarely sought) ----------

    /// <summary>When the definition row was first registered. Set server-side.</summary>
    [DbColumn("created_at_utc", DbKind.UtcInstant, Default = DbDefault.UtcNow)]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When the definition row was last updated (registration or operator override edit). Set
    /// server-side on every mutation; keys the per-namespace definition reload tick.
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
