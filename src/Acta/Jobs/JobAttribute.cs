namespace Acta;

/// <summary>
/// Declares a <c>JobDefinition</c> on a handler method, registering it with the source generator.
/// The operator-facing kebab-case <see cref="Name"/> is the identity copied into SQL, dashboards, and alerts.
/// </summary>
/// <remarks>
/// Two knobs are deliberately absent. No <c>LeaseTtl</c>: the lease window is a single
/// worker-wide value (<c>JobsOptions.LeaseTtlSeconds</c>) that the heartbeat refreshes while a
/// handler runs, and a per-definition policy would re-add a JOIN on the hot claim path. No
/// per-definition <c>ExecutionRetention</c>: all <c>JobEvent</c> rows honor the single cluster
/// knob <c>JobsOptions.JobEventsRetention</c>.
/// </remarks>
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
    /// How many attempts of this definition may execute at once, 1..1024. Unset means no limit unless
    /// an enqueue supplied a <c>ConcurrencyKey</c>, which alone means 1. The gate is the key: the
    /// enqueue's key when there is one, otherwise the definition name, so two definitions that share a
    /// key share its slots. Shared-key capacity is the largest limit among the participants; a
    /// smaller-limit definition competes for the first N slots only, so lowering one definition never
    /// reduces another participant's slots. A changed limit reaches a worker on its next definition
    /// policy reload, so admissions may use the old limit until every worker has observed the change.
    /// See <see cref="RateLimit"/> for the other half of admission control: how often, rather than how many.
    /// </summary>
    public short ConcurrencyLimit { get; init; }

    /// <summary>
    /// How often attempts of this definition may start, cluster-wide, as <c>N/s</c>, <c>N/m</c>, or
    /// <c>N/h</c> (<c>"10/s"</c>). Null means no rate limit. N is a positive whole number and the rate
    /// may not exceed 1000 per second. Admission is a reservation, not a retry loop: a job that arrives
    /// early is re-armed exactly once, at the instant the meter will admit it, so a backlog drains at
    /// the rate with one re-arm per job. An idle meter admits one second's worth of the rate at once,
    /// at least one (<c>600/m</c> bursts ten, then one per 100 ms), and allocates at most
    /// <c>R*T + B</c> turns in any <c>T</c> seconds for that burst <c>B</c>; a turn may be taken up to
    /// one interval late, so any window may see one more: at most <c>R*T + B + 1</c>. Rate and
    /// <see cref="ConcurrencyLimit"/> are independent gates
    /// and a job passes both; the concurrency slot is taken first and released when the rate denies.
    /// The meter is per <see cref="RateKey"/>, and the limit is an operator-overridable policy slot.
    /// </summary>
    public string? RateLimit { get; init; }

    /// <summary>
    /// Which meter <see cref="RateLimit"/> spends from, kebab-case; null means the definition name, so a
    /// rate alone throttles just this definition. Definitions sharing a key share one meter and must
    /// declare the same rate, which worker startup enforces. Unlike the rate itself the key is code-owned
    /// and has no operator override: moving a definition to another meter changes who it competes with,
    /// which is a contract change, not a dial.
    /// </summary>
    public string? RateKey { get; init; }

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
    /// <see cref="DeadlineBehavior"/> for what the engine does when it is exceeded. Cannot be combined
    /// with <c>[JobSchedule]</c>: a recurring slot is created once and lives forever, so a deadline
    /// anchored to its creation could never bound an occurrence, and worker startup rejects the pair.
    /// </summary>
    public string? Deadline { get; init; }

    /// <summary>
    /// How the engine treats a job past its <see cref="Deadline"/>. Default <c>Strict</c>. Only
    /// meaningful when <see cref="Deadline"/> is set.
    /// </summary>
    public DeadlineBehaviorCode DeadlineBehavior { get; init; } = DeadlineBehaviorCode.Strict;

    /// <summary>
    /// How long terminal Jobs are retained before the retention sweep deletes them, in Acta duration
    /// syntax, e.g. <c>"90d"</c> (the default) or <c>"6h"</c>. <c>"0s"</c> means purge at the next
    /// sweep. Null = framework default.
    /// </summary>
    public string? JobRetention { get; init; }

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
