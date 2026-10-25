namespace Acta;

/// <summary>
/// What a typed enqueue answers: the <see cref="EnqueueJobResponse"/> plus the reads and verbs that follow the job,
/// each forwarded to <see cref="IJobs"/> by its committed id, so none pays an extra lookup. Being the response, it
/// passes wherever one is taken. <see cref="IJobs.GetHandleAsync"/> rebuilds one from a ref in another process.
/// </summary>
public record JobHandle : EnqueueJobResponse
{
    private readonly IJobs _jobs;

    /// <summary>A handle on a committed job; a test double of <see cref="IJobs"/> builds one directly.</summary>
    public JobHandle(IJobs jobs, long jobId, JobRef jobRef, EnqueueJobAction action)
        : base(jobId, jobRef, action)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _jobs = jobs;
    }

    /// <inheritdoc cref="IJobs.GetStatusAsync"/>
    public ValueTask<GetJobStatusResponse?> GetStatusAsync(CancellationToken ct = default) => _jobs.GetStatusAsync(this, ct);

    /// <inheritdoc cref="IJobs.WaitAsync"/>
    public ValueTask<JobOutcome> WaitAsync(TimeSpan? timeout = null, CancellationToken ct = default) => _jobs.WaitAsync(this, timeout, ct);

    /// <inheritdoc cref="IJobs.WaitAsync{TResult}"/>
    public ValueTask<JobOutcome<TResult>> WaitAsync<TResult>(TimeSpan? timeout = null, CancellationToken ct = default)
        where TResult : notnull => _jobs.WaitAsync<TResult>(this, timeout, ct);

    /// <inheritdoc cref="IJobs.GetResultAsync{TResult}"/>
    public ValueTask<TResult?> GetResultAsync<TResult>(CancellationToken ct = default) => _jobs.GetResultAsync<TResult>(this, ct);

    /// <inheritdoc cref="IJobs.CancelAsync"/>
    public ValueTask<CancelJobResponse> CancelAsync(string? reasonMessage = null, string? actor = null, CancellationToken ct = default) =>
        _jobs.CancelAsync(this, reasonMessage, actor, ct: ct);

    /// <inheritdoc cref="IJobs.ExplainAsync"/>
    public ValueTask<GetJobExplanationResponse?> ExplainAsync(CancellationToken ct = default) => _jobs.ExplainAsync(this, ct);
}

/// <summary>
/// A <see cref="JobHandle"/> from a result-bearing contract: <see cref="WaitAsync"/> and <see cref="GetResultAsync"/>
/// answer the job's <typeparamref name="TResult"/> without naming it again.
/// </summary>
public record JobHandle<TResult> : JobHandle
    where TResult : notnull
{
    /// <inheritdoc cref="JobHandle(IJobs, long, JobRef, EnqueueJobAction)"/>
    public JobHandle(IJobs jobs, long jobId, JobRef jobRef, EnqueueJobAction action)
        : base(jobs, jobId, jobRef, action) { }

    /// <inheritdoc cref="IJobs.WaitAsync{TResult}"/>
    public new ValueTask<JobOutcome<TResult>> WaitAsync(TimeSpan? timeout = null, CancellationToken ct = default) =>
        WaitAsync<TResult>(timeout, ct);

    /// <inheritdoc cref="IJobs.GetResultAsync{TResult}"/>
    public ValueTask<TResult?> GetResultAsync(CancellationToken ct = default) => GetResultAsync<TResult>(ct);
}
