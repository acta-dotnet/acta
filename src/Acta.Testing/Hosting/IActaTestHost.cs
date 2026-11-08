namespace Acta.Testing;

/// <summary>
/// A running Acta runtime for tests: enqueue jobs, drive them deterministically with
/// <see cref="RunOnceAsync"/>, and assert through the <see cref="Jobs"/> read surface. Provider-agnostic:
/// the integrator wires their own <c>UseSqlServer</c> / <c>UsePostgres</c> in the start callback.
/// </summary>
public interface IActaTestHost : IAsyncDisposable
{
    /// <summary>The schema this host's runtime targets.</summary>
    string Schema { get; }

    /// <summary>The built service provider: escape hatch for resolving anything the runtime registered.</summary>
    IServiceProvider Services { get; }

    /// <summary>The public job control / enqueue surface.</summary>
    IJobs Jobs { get; }

    /// <summary>The operator facade (list reads, domain facades, overview).</summary>
    IActaOperations Operations { get; }

    /// <summary>Claim + execute at most one job in <paramref name="jobNamespace"/>; returns what happened.</summary>
    Task<ActaRunStatus> RunOnceAsync(string jobNamespace, CancellationToken ct = default);

    /// <summary>
    /// Claim + execute the specific <paramref name="job"/>, retrying a transient claim miss under parallel load.
    /// The enqueue's <see cref="JobHandle"/> or <see cref="EnqueueJobResponse"/> and a <see cref="JobRef"/> convert
    /// to the lookup, as for every <see cref="IJobs"/> call. Multi-worker hosts route by the job's own namespace.
    /// A ref or deduplication key that matches no job throws, here and in every helper that takes a lookup.
    /// </summary>
    Task<ActaRunStatus> RunOnceAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>Run one framework recovery pass for <paramref name="jobNamespace"/>.</summary>
    Task<ActaRecoveryReport> RunRecoveryOnceAsync(string jobNamespace, CancellationToken ct = default);

    /// <summary>Move the job's next-run instant into the past without otherwise changing its state.</summary>
    Task ForceJobDueAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>Move the next pending timer checkpoint, or the named pending timer, into the past.</summary>
    Task ForceTimerDueAsync(JobLookup job, string? name = null, CancellationToken ct = default);

    /// <summary>
    /// Move a bounded signal or child wait's stored expiration, and the job's next-run instant, into
    /// the past so the next tick claims the Suspended job and resolves the wait as timed out. Throws
    /// when the named wait (or any pending wait) carries no expiration, because an unbounded wait can
    /// never time out and a silent no-op would look like a passing test.
    /// </summary>
    Task ForceWaitTimeoutDueAsync(JobLookup job, string? name = null, CancellationToken ct = default);

    /// <summary>
    /// The group twin of <see cref="ForceWaitTimeoutDueAsync"/>: move the job's stored group wait
    /// deadline, every child latch it has armed, and the job's next-run instant into the past, so the
    /// next tick claims the Suspended job and the group resolves timed out. A group spends one stored
    /// deadline rather than one per latch, so rewinding the latches alone would leave a member armed
    /// after the rewind counting down from a fresh budget. Takes no wait name because the deadline is
    /// the group's, not any one child's. Throws when the job stores no group deadline, because an
    /// unbounded group wait can never time out and a silent no-op would look like a passing test.
    /// </summary>
    Task ForceGroupWaitTimeoutDueAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>Expire the current execution lease for a claimed job.</summary>
    Task ExpireExecutionLeaseAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Move the schedule's next occurrence and its next evaluation to moments ago, inside the misfire grace,
    /// so the next <see cref="AdmitOnceAsync"/> admits it under its own policy. A paused schedule stays paused;
    /// throws when the schedule does not exist, and for a schedule bound to a calendar, which admits only the
    /// instants the calendar produces: advance the host's clock to one, or fire the permanent job with
    /// <see cref="ISchedules.TriggerAsync"/>.
    /// </summary>
    Task ForceScheduleDueAsync(ScheduleLookup schedule, CancellationToken ct = default);

    /// <summary>
    /// Run one admission pass over <paramref name="jobNamespace"/>: every schedule that is due is evaluated, and
    /// what the policies select is admitted, a permanent job from <c>Idle</c> to <c>Ready</c> or a fresh root per
    /// occurrence, for <see cref="RunOnceAsync(JobLookup, CancellationToken)"/> to run. The host runs no admitter
    /// loop, so a scheduled job fires only through this.
    /// </summary>
    Task<ActaAdmissionReport> AdmitOnceAsync(string jobNamespace, CancellationToken ct = default);
}

/// <summary>
/// Coarse outcome of one <see cref="IActaTestHost.RunOnceAsync"/> tick: the public mirror of the
/// runtime's internal run outcome, so integrators can assert on what a single drive did.
/// </summary>
public enum ActaRunStatus : byte
{
    /// <summary>Nothing was claimable this tick (no Ready job, or a claim lost its row before start).</summary>
    NothingClaimed = 1,

    /// <summary>
    /// A job ran to terminal <c>Succeeded</c>, or the run was cancelled; a permanent job's success settles its
    /// firing and returns the job to <c>Idle</c> for its next admission.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// A job ran and settled terminal <c>Failed</c>: its budget spent, or a non-retryable exception. A permanent job's
    /// firing that spends its budget reports this too, while the job returns to <c>Idle</c> for its next admission.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The job re-armed for a later claim: it rescheduled, slept, paused, suspended on a signal, or threw
    /// with budget left (that retry spends budget), or a concurrency or rate limit bounced it.
    /// </summary>
    Rearmed = 4,
}

/// <summary>
/// What one test-driven recovery pass for a namespace changed.
/// </summary>
public sealed record ActaRecoveryReport(int DeadWorkersMarked, int ReclaimedJobs, int ReleasedChildLatches);

/// <summary>What one test-driven admission pass for a namespace admitted: permanent-job firings and per-occurrence roots together.</summary>
public sealed record ActaAdmissionReport(int Admitted);
