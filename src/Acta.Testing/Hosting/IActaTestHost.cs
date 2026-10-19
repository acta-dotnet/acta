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
    /// Claim + execute the specific job <paramref name="jobId"/> (by id), retrying a transient claim
    /// miss under parallel load. Multi-worker hosts route by the job's own namespace.
    /// </summary>
    Task<ActaRunStatus> RunOnceAsync(long jobId, CancellationToken ct = default);

    /// <summary>Convenience overload taking the enqueue result directly: <c>RunOnceAsync(enqueued, ct)</c>.</summary>
    Task<ActaRunStatus> RunOnceAsync(EnqueueJobResponse enqueued, CancellationToken ct = default);

    /// <summary>Run one framework recovery pass for <paramref name="jobNamespace"/>.</summary>
    Task<ActaRecoveryReport> RunRecoveryOnceAsync(string jobNamespace, CancellationToken ct = default);

    /// <summary>Move the job's next-run instant into the past without otherwise changing its state.</summary>
    Task ForceJobDueAsync(long jobId, CancellationToken ct = default);

    /// <summary>Move the next pending timer checkpoint, or the named pending timer, into the past.</summary>
    Task ForceTimerDueAsync(long jobId, string? name = null, CancellationToken ct = default);

    /// <summary>Move the next pending step retry, or the named pending step retry, into the past.</summary>
    Task ForceStepRetryDueAsync(long jobId, string? name = null, CancellationToken ct = default);

    /// <summary>
    /// Move a bounded signal or child wait's stored expiration, and the job's next-run instant, into
    /// the past so the next tick claims the Suspended job and resolves the wait as timed out. Throws
    /// when the named wait (or any pending wait) carries no expiration, because an unbounded wait can
    /// never time out and a silent no-op would look like a passing test.
    /// </summary>
    Task ForceWaitTimeoutDueAsync(long jobId, string? name = null, CancellationToken ct = default);

    /// <summary>
    /// The group twin of <see cref="ForceWaitTimeoutDueAsync"/>: move the job's stored group wait
    /// deadline, every child latch it has armed, and the job's next-run instant into the past, so the
    /// next tick claims the Suspended job and the group resolves timed out. A group spends one stored
    /// deadline rather than a per-slot one, so rewinding the latches alone would leave a member armed
    /// after the rewind counting down from a fresh budget. Takes no wait name because the deadline is
    /// the group's, not any one child's. Throws when the job stores no group deadline, because an
    /// unbounded group wait can never time out and a silent no-op would look like a passing test.
    /// </summary>
    Task ForceGroupWaitTimeoutDueAsync(long jobId, CancellationToken ct = default);

    /// <summary>Expire the current execution lease for a claimed job.</summary>
    Task ExpireExecutionLeaseAsync(long jobId, CancellationToken ct = default);
}
