using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta;

/// <summary>
/// Handler-facing per-attempt context (<c>ctx</c>): the job's identity, the per-attempt cancellation token, and the
/// durable operations (progress, variables, sleeps, signals, children, steps, locks, alerts, notes). The framework
/// supplies it: <c>RuntimeJobContext</c> is the production implementation, only Acta's own assemblies derive from it,
/// and a test drives a handler through the <c>Acta.Testing</c> host. Steps and signals are durable and replay-safe,
/// timers suspend the job budget-neutrally, and a concrete subclass supplies the storage behind each <c>Core</c> sink.
/// </summary>
public abstract partial class JobContext
{
    private protected JobContext() { }

    /// <summary>Public Job handle (the sequence-allocated <c>job.id</c>).</summary>
    public abstract long JobId { get; }

    /// <summary>Owning <c>JobNamespace</c> (kebab).</summary>
    public abstract string JobNamespace { get; }

    /// <summary>
    /// Owning <c>JobNamespace</c> id: the DB-assigned numeric id, not the kebab
    /// <see cref="JobNamespace"/> name. Used by system jobs that invoke namespace-scoped routines.
    /// </summary>
    public abstract int NamespaceId { get; }

    /// <summary>
    /// Optional tenant this Job is <em>about</em>: the DB-assigned <c>tenants</c> id resolved from the
    /// enqueue <c>TenantName</c>, or <c>null</c> when the Job has no tenant (including system Jobs).
    /// Inherited from the parent on child Jobs unless the child supplied its own tenant. Read-only scope
    /// for handler branching; it is not a claim or scheduling key.
    /// </summary>
    public virtual int? TenantId => null;

    /// <summary>
    /// External tenant name of <see cref="TenantId"/> (the enqueue <c>TenantName</c>, a folded kebab-case name), or
    /// <c>null</c> when the Job has no tenant. Resolved once per distinct tenant per process and
    /// cached; the authoritative identity for tenant-aware application services during execution.
    /// </summary>
    public virtual string? TenantName => null;

    /// <summary>Descriptor name (kebab).</summary>
    public abstract string JobName { get; }

    /// <summary>
    /// The job's public stable reference. Default <c>default(JobRef)</c> for test contexts that do
    /// not set it.
    /// </summary>
    public virtual JobRef JobRef => default;

    /// <summary>
    /// Per-attempt cancellation token; cancels on lease expiry, host shutdown, or descriptor
    /// <c>ExecutionTimeout</c>.
    /// </summary>
    public abstract CancellationToken CancellationToken { get; }

    /// <summary>
    /// For a recurring fire, the names of the schedules that were due and coalesced into this
    /// execution, ordered by name. Empty for non-recurring jobs.
    /// </summary>
    public virtual IReadOnlyList<string> TriggeringScheduleNames => [];

    /// <summary>
    /// Which attempt this is: <c>1</c> on the first execution, incrementing on every retry and on every reclaim after
    /// a worker died mid-attempt. The same number the ledger records as <c>execution_number</c>, so
    /// <c>(JobId, ExecutionNumber)</c> identifies this attempt in the timeline and makes a stable idempotency key for
    /// an external call. Defaults to <c>1</c> for test contexts that do not set it.
    /// </summary>
    public virtual int ExecutionNumber => 1;

    /// <summary>
    /// The <c>workers</c> row id of the worker process running this attempt, for log correlation and
    /// for a handler that wants to stamp its own execution into a note. <c>0</c> when no worker owns
    /// the context, which is the case only in test contexts. Not an affinity handle: the next attempt
    /// of this job may run on any worker, and nothing in Acta promises otherwise.
    /// </summary>
    public virtual int WorkerId => 0;

    /// <summary>
    /// Absolute UTC deadline for the whole job (job creation plus the definition's deadline), or null
    /// when no deadline is configured. Anchored to job creation, so it is stable across retries.
    /// </summary>
    public virtual DateTime? DeadlineAtUtc => null;

    /// <summary>
    /// Whether the job is past its deadline now. False when no deadline is configured.
    /// </summary>
    public bool IsOverdue => DeadlineAtUtc is { } due && due <= DateTime.UtcNow;

    /// <summary>
    /// Time remaining until the deadline; negative when overdue, null when no deadline is configured.
    /// </summary>
    public TimeSpan? TimeUntilDeadline => DeadlineAtUtc is { } due ? due - DateTime.UtcNow : null;

    /// <summary>
    /// Re-arms this Job to run again after <paramref name="delay"/> and stops the current attempt without charging the
    /// failure budget; the host computes the due instant as <c>db_now + delay</c>. Throws
    /// <see cref="RescheduleJobException"/> synchronously, so code after the call is unreachable and a forgotten
    /// <c>await</c> still stops the handler; the <see cref="Task"/> return mirrors <see cref="SleepAsync"/>.
    /// </summary>
    public Task RescheduleAsync(TimeSpan delay, string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new RescheduleJobException(delay, reasonMessage);
    }

    /// <summary>
    /// Absolute-instant variant of <see cref="RescheduleAsync"/>: re-arms this Job to run again at
    /// <paramref name="resumeAtUtc"/>. A past instant re-arms as immediately claimable; throw and
    /// budget semantics match <see cref="RescheduleAsync"/>.
    /// </summary>
    public Task RescheduleUntilAsync(DateTimeOffset resumeAtUtc, string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new RescheduleJobException(resumeAtUtc, reasonMessage);
    }

    /// <summary>
    /// Deliberately ends this Job as terminal <c>Failed</c> for a business reason: no retry, failure budget untouched,
    /// unlike a thrown exception, which is recorded as an unhandled failure and retried. Throws
    /// <see cref="HandlerFailException"/> synchronously, so code after the call is unreachable and a forgotten
    /// <c>await</c> still stops the handler; the <see cref="Task"/> return mirrors the other control verbs.
    /// </summary>
    public Task FailAsync(string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new HandlerFailException(reasonMessage);
    }

    /// <summary>
    /// Deliberately ends this Job as terminal <c>Cancelled</c>, a non-failure stop (duplicate,
    /// superseded, not-applicable work). Throws <see cref="HandlerCancelException"/> synchronously; code
    /// after this call in the current attempt is unreachable. The attempt is not retried and the failure
    /// budget is untouched. The cancel cascades recursively to this Job's non-terminal descendant
    /// subtree (descendants land <c>Cancelled</c> with <c>reason = ParentCancelled</c>).
    /// </summary>
    public Task CancelAsync(string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new HandlerCancelException(reasonMessage);
    }

    /// <summary>
    /// Deliberately holds this Job in <c>Paused</c> until an external resume (manual review, missing
    /// approval, operator intervention). Throws <see cref="HandlerPauseException"/> synchronously; code
    /// after this call in the current attempt is unreachable. The Job is not retried automatically, sets
    /// no <c>next_run_at_utc</c>, and the failure budget is untouched. Resumes only through
    /// <c>IJobs.ResumeAsync</c>. This is operator-driven hold, not framework-managed
    /// <c>Suspended</c> (which <see cref="SleepAsync"/> / <see cref="WaitSignalAsync(string, CancellationToken)"/> produce).
    /// </summary>
    public Task PauseAsync(string? reasonMessage = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        throw new HandlerPauseException(reasonMessage);
    }

    /// <summary>
    /// Persists an operator-facing alert from inside the handler, stamped <c>Manual</c> origin and reason, delivery
    /// <c>Pending</c>, and this job's namespace and job. A non-null <paramref name="deduplicationKey"/> collapses
    /// repeats onto the one unresolved row carrying it (<c>occurrence_count</c>++, content refreshed) and opens a
    /// fresh row once that one is resolved; a null key always inserts. A null <paramref name="channelName"/> routes to
    /// the <c>default</c> channel. Writing the row is independent of delivery; title and message are capped to column
    /// width at persistence, and <paramref name="ct"/> is linked with the per-attempt token.
    /// </summary>
    public async Task AlertAsync(
        string title,
        string message,
        AlertSeverityCode severityCode,
        string? channelName = null,
        string? deduplicationKey = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (channelName is not null)
        {
            channelName = NameSyntax.Canonicalize(channelName, nameof(channelName), maxLength: NameSyntax.ExtendedMaxLength);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await RaiseAlertCoreAsync(severityCode, title, message, channelName, deduplicationKey, linked.Token);
    }

    /// <summary>
    /// Subclass sink: persist the alert. Bounded text fields are truncated to their column widths and
    /// the deduplication key is normalized in the concrete implementation.
    /// </summary>
    private protected abstract Task RaiseAlertCoreAsync(
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    );

    /// <summary>
    /// Writes an application-authored note onto this job's timeline, beside the framework's own events rather than in
    /// a log sink not joined to the job row. Annotation, not logging: <c>events</c> is indexed for timeline queries,
    /// so use <c>ILogger</c> for volume. Notes ignore <c>AuditLevel</c>, a volume control over events Acta chooses to
    /// record, because dropping an explicit call would be data loss; bounding a job's event volume is the caller's.
    /// Notes are the only event code an application can write and the runtime never emits, so an application can
    /// annotate the ledger but never forge <c>job.execution-finished</c>; do not widen this to a second code.
    /// </summary>
    public async Task NoteAsync(string message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await WriteNoteCoreAsync<object>(message, null, linked.Token);
    }

    /// <inheritdoc cref="NoteAsync(string, CancellationToken)"/>
    /// <param name="message">The note line; truncated to the event message column width.</param>
    /// <param name="detail">
    /// Structured context, stored as JSON. Bounded by <c>JobsOptions.MaxInlinePayloadBytes</c>, the
    /// same ceiling as every other caller-controlled write, and a payload past it throws
    /// <see cref="PayloadTooLargeException"/> rather than truncating or surfacing a driver error.
    /// </param>
    /// <param name="ct">Cancellation token, linked to the job's own.</param>
    public async Task NoteAsync<T>(string message, T detail, CancellationToken ct = default)
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(detail);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await WriteNoteCoreAsync(message, detail, linked.Token);
    }

    /// <summary>
    /// Subclass sink: append the note as a <c>job.note-recorded</c> event. Generic so the subclass serializes
    /// through its payload serializer and source-generated JSON is honored under Native AOT. The
    /// message is truncated to the column width in the concrete implementation.
    /// </summary>
    private protected abstract Task WriteNoteCoreAsync<T>(string message, T? detail, CancellationToken ct);
}
