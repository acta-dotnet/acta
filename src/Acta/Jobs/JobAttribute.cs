namespace Acta;

/// <summary>
/// Declares a <c>JobDefinition</c> on a handler method, registering it with the source generator.
/// The operator-facing kebab-case <see cref="Name"/> is the identity copied into SQL, dashboards, and alerts.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class JobAttribute(string name) : Attribute
{
    /// <summary>
    /// Required kebab-case JobName. Max 128 chars.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Limits consecutive failures, not total invocations. <c>Reschedule</c>, <c>Suspend</c>, and
    /// <c>Pause</c> never consume the budget.
    /// </summary>
    public short MaxAttempts { get; init; } = 15;

    /// <summary>
    /// Strict ordering on claim, with no aging or anti-starvation budget.
    /// </summary>
    public JobPriorityCode Priority { get; init; } = JobPriorityCode.Normal;

    /// <summary>
    /// Retry backoff policy, e.g. <c>"1m..8h x2 +-10%"</c>. Null = framework default.
    /// </summary>
    public string? Backoff { get; init; }

    /// <summary>
    /// Per-attempt wall-clock cap. Cancels the handler's <c>CancellationToken</c> when exceeded; does not
    /// span retries. Use Acta duration syntax, e.g. <c>"30s"</c>.
    /// </summary>
    public string? ExecutionTimeout { get; init; }

    /// <summary>
    /// Whole-job wall-clock deadline measured from job creation. Use Acta duration syntax, e.g. <c>"2h"</c>.
    /// Unlike <see cref="ExecutionTimeout"/> it spans retries. Null = no deadline. See
    /// <see cref="DeadlineBehavior"/> for what the engine does when it is exceeded.
    /// </summary>
    public string? Deadline { get; init; }

    /// <summary>
    /// How the engine treats a job past its <see cref="Deadline"/>. Default <c>Strict</c>. Only
    /// meaningful when <see cref="Deadline"/> is set.
    /// </summary>
    public DeadlineBehaviorCode DeadlineBehavior { get; init; } = DeadlineBehaviorCode.Strict;

    // No LeaseTtl knob: the lease window is a single worker-wide value
    // (JobsOptions.LeaseTtlSeconds, default 180s) that WorkerHeartbeat refreshes while a
    // handler runs. A per-definition policy here would re-add a JOIN on the hot claim path.

    /// <summary>
    /// How long terminal Jobs are retained before the retention sweep deletes them, in Acta duration
    /// syntax, e.g. <c>"90d"</c> (the default) or <c>"6h"</c>. <c>"0s"</c> means purge at the next
    /// sweep. Null = framework default.
    /// </summary>
    public string? JobRetention { get; init; }

    // No per-definition ExecutionRetention: all JobEvent rows honor the single cluster knob
    // JobsOptions.JobEventsRetention.

    /// <summary>
    /// Audit emission level; gates audit-filtered per-job <c>JobEvent</c> writes but not the alert
    /// MERGE. When left unset the generator applies the framework default (<c>Audit</c>). Sub-minute
    /// recurring definitions should consider <c>Off</c> or <c>Failures</c> to keep <c>events</c> lean.
    /// </summary>
    public JobAuditLevelCode AuditLevel { get; init; } = JobAuditLevelCode.Audit;

    /// <summary>
    /// Whether jobs of this definition must, may, or must not carry a tenant. Persisted on the
    /// definition and enforced at the enqueue boundary in the database: <c>Required</c> rejects a
    /// tenant-less enqueue (explicit TenantKey or parent inheritance both satisfy it), and
    /// <c>Forbidden</c> rejects an explicit TenantKey and suppresses parent inheritance.
    /// </summary>
    public JobTenantRequirementCode TenantRequirement { get; init; } = JobTenantRequirementCode.Optional;

    /// <summary>
    /// For a recurring definition, the maximum <c>results</c> rows retained on the slot (newest
    /// by execution). Definition/runtime metadata only; never persisted as a <c>jobs</c> column.
    /// </summary>
    public int RecurringResultCap { get; init; } = 1;

    /// <summary>
    /// Kebab-case payload format applied to both this Job's input and result bytes. Built-in formats
    /// are <c>"json"</c>, <c>"text"</c>, <c>"bytes"</c>; operator-supplied formats use their
    /// <see cref="JobPayloadFormatDeclarationAttribute.Name"/>. Mutually exclusive with
    /// <see cref="InputFormat"/> and <see cref="OutputFormat"/>. Null means the generator infers each
    /// side from its CLR shape. On a void-returning handler it applies to the input only.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Kebab-case payload format for this Job's input bytes only. Same naming rules as
    /// <see cref="Format"/>; null infers it from the input shape (Json for complex inputs and
    /// non-string scalars, Text for <c>string</c>, Bytes for <c>byte[]</c> and
    /// <c>ReadOnlyMemory&lt;byte&gt;</c>).
    /// </summary>
    public string? InputFormat { get; init; }

    /// <summary>
    /// Kebab-case payload format for the handler's result bytes only. Same naming rules as
    /// <see cref="Format"/>; null infers it from the return type (records, classes, and DTO structs use
    /// Json; scalars, strings, and enums use Text; <c>byte[]</c> and <c>ReadOnlyMemory&lt;byte&gt;</c>
    /// use Bytes). Has no effect on void-returning handlers.
    /// </summary>
    public string? OutputFormat { get; init; }

    /// <summary>
    /// Search tags persisted as <c>Tag</c> rows. Each entry is <c>"name"</c> (presence-only) or
    /// <c>"name=value"</c>; names are kebab-case <c>varchar(64)</c>, values <c>varchar(128)</c>.
    /// Read-only after enqueue. Canonicalization rules
    /// live on the <c>Tag</c> entity XML docs.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// Automatic-alert profile; default <c>OnFailure</c> for user Jobs. The transition-to-severity
    /// mapping lives in the <c>AlertProfileCode</c> code-family XML docs.
    /// </summary>
    public AlertProfileCode AlertProfile { get; init; } = AlertProfileCode.OnFailure;

    /// <summary>
    /// Operator-stable alert channel name to which this Job's alerts route.
    /// </summary>
    public string? AlertChannelName { get; init; }

    /// <summary>
    /// Runbook URL surfaced on alerts and the operator dashboard.
    /// </summary>
    public string? RunbookUrl { get; init; }

    /// <summary>Human display label surfaced on the operator dashboard; falls back to the job name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Operator-facing description of what the job does.</summary>
    public string? Description { get; init; }
}
