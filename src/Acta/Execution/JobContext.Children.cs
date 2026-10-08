namespace Acta;

public abstract partial class JobContext
{
    private const string ChildSignalPrefix = "sys.child.";

    /// <summary>
    /// Enqueues a child job from a typed input, replay-safe: <paramref name="name"/> is the child's deduplication key,
    /// so a replay returns the existing child (<see cref="EnqueueJobAction.Deduplicated"/>). Join with <see cref="WaitChildAsync"/>.
    /// </summary>
    /// <param name="name">Stable kebab-case child name, unique among this Job's children.</param>
    /// <param name="input">Typed input resolved to a job route via the generated manifest.</param>
    /// <param name="configure">Optional enqueue options; the parent id and deduplication key are framework-set and win.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public async Task<EnqueueJobResponse> StartChildAsync<TInput>(
        string name,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        ArgumentNullException.ThrowIfNull(input);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await StartChildCoreAsync(input, BuildChildOptions(name, configure), linked.Token);
    }

    /// <summary>
    /// <see cref="StartChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/> with
    /// <paramref name="ct"/> in the third slot: no <c>configure</c>, no named <c>ct: ct</c>.
    /// </summary>
    /// <param name="name">Stable kebab-case child name, unique among this Job's children.</param>
    /// <param name="input">Typed input resolved to a job route via the generated manifest.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<EnqueueJobResponse> StartChildAsync<TInput>(string name, TInput input, CancellationToken ct)
        where TInput : notnull => StartChildAsync(name, input, configure: null, ct: ct);

    /// <summary>
    /// Raw-route variant of
    /// <see cref="StartChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    /// for explicit
    /// (<paramref name="jobNamespace"/>, <paramref name="jobName"/>) targeting, including a namespace
    /// other than this Job's.
    /// </summary>
    public async Task<EnqueueJobResponse> StartChildAsync(
        string name,
        string jobNamespace,
        string jobName,
        JobPayload input = default,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = IdentifierSyntax.CanonicalizeUserKebab(name, nameof(name), IdentifierSyntax.ExtendedMaxLength);
        var options = BuildChildOptions(name, configure);
        var request = new EnqueueJobRequest(
            jobNamespace,
            jobName,
            input,
            DeduplicationKey: options.DeduplicationKey,
            CorrelationKey: options.CorrelationKey,
            ConcurrencyKey: options.ConcurrencyKey,
            Priority: options.Priority,
            NextRunAtUtc: options.NextRunAtUtc,
            DelaySeconds: options.DelaySeconds,
            Tags: options.Tags,
            ParentJobId: JobId,
            TenantKey: options.TenantKey,
            OverrideParentTenant: options.OverrideParentTenant,
            Lane: options.Lane
        );

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await StartChildCoreAsync(request, linked.Token);
    }

    /// <summary>
    /// Reads a child's stored result, deserialized to <typeparamref name="TResult"/>; <c>default</c>
    /// when the child stored none or its result row was already purged by retention, which keeps a
    /// completed child while this job is not terminal. Point-in-time and non-blocking; wait for the
    /// child first when ordering matters.
    /// </summary>
    public async Task<TResult?> GetChildResultAsync<TResult>(long childJobId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childJobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await GetChildResultCoreAsync<TResult>(childJobId, linked.Token);
    }

    /// <summary>
    /// Starts the named child and durably waits for its terminal outcome in one call, the child-level
    /// mirror of <c>IJobs.RunAndWaitAsync</c>. Returned, never thrown; branch on the outcome or call
    /// <see cref="JobOutcome.ThrowIfFailed"/>. Waits this one child before returning: to fan out, start
    /// every child first and join with
    /// <see cref="WaitChildrenAsync(IReadOnlyList{long}, CancellationToken)"/>.
    /// </summary>
    public async Task<JobOutcome> ExecuteChildAsync<TInput>(
        string name,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        var child = await StartChildAsync(name, input, configure, ct);
        var outcome = await WaitChildAsync(child.JobId, ct);
        return ToJobOutcome(child.JobId, outcome.Status);
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome> ExecuteChildAsync<TInput>(string name, TInput input, CancellationToken ct)
        where TInput : notnull => ExecuteChildAsync(name, input, configure: null, ct: ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// the child is started, then awaited under one stored expiration (<c>db_now + timeout</c>, whole seconds,
    /// positive) that a replay reuses and never extends. On expiry the outcome reports
    /// <see cref="JobOutcome.IsTimedOut"/>, Acta cancels the child and its descendant subtree, and this Job resumes.
    /// </summary>
    public async Task<JobOutcome> ExecuteChildAsync<TInput>(
        string name,
        TInput input,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        // Checked before the child is started, not when the wait reaches it: a rejected timeout must not
        // leave an enqueued child behind that nothing is going to wait for.
        ToWaitTimeoutSeconds(timeout);
        var child = await StartChildAsync(name, input, configure, ct);
        // One child needs no group deadline slot: the latch's own stored expiration already is the one
        // persisted absolute instant, and a second copy of it could only drift.
        var result = await TryWaitChildAsync(child.JobId, timeout, ct);
        return result.Outcome is { } outcome
            ? ToJobOutcome(child.JobId, outcome.Status)
            : JobOutcome.TimedOut(child.JobId, TimedOutChildStatus);
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the fourth positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome> ExecuteChildAsync<TInput>(string name, TInput input, TimeSpan timeout, CancellationToken ct)
        where TInput : notnull => ExecuteChildAsync(name, input, timeout, configure: null, ct: ct);

    private static JobOutcome ToJobOutcome(long childJobId, JobStatusCode status) =>
        status switch
        {
            JobStatusCode.Succeeded => JobOutcome.Succeeded(childJobId),
            JobStatusCode.Cancelled => JobOutcome.Cancelled(childJobId),
            _ => JobOutcome.Failed(childJobId),
        };

    /// <summary>
    /// What a timed-out wait leaves the child in: Acta cancels it before the handler resumes, so
    /// the outcome reports Cancelled beside the timeout flag rather than inventing a status of its
    /// own.
    /// </summary>
    private const JobStatusCode TimedOutChildStatus = JobStatusCode.Cancelled;

    /// <summary>
    /// Result-returning variant of
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// on a successful child the
    /// outcome carries the deserialized result (<see cref="JobOutcome{T}.ValueOrThrow"/> for the terse
    /// path). A child that succeeded without storing a result throws: the caller asked for a typed
    /// result the child's contract does not produce.
    /// </summary>
    public async Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(
        string name,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        var child = await StartChildAsync(name, input, configure, ct);
        var outcome = await WaitChildAsync(child.JobId, ct);
        if (outcome.Status != JobStatusCode.Succeeded)
        {
            return outcome.Status == JobStatusCode.Cancelled
                ? JobOutcome<TResult>.Cancelled(child.JobId)
                : JobOutcome<TResult>.Failed(child.JobId);
        }

        return JobOutcome<TResult>.Succeeded(child.JobId, await RequireChildResultAsync<TResult>(child.JobId, name, ct));
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="ExecuteChildAsync{TInput, TResult}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(string name, TInput input, CancellationToken ct)
        where TInput : notnull
        where TResult : notnull => ExecuteChildAsync<TInput, TResult>(name, input, configure: null, ct: ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="ExecuteChildAsync{TInput, TResult}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// the child is started, then awaited under one stored expiration (<c>db_now + timeout</c>, whole seconds,
    /// positive) that a replay reuses and never extends. On expiry the outcome reports
    /// <see cref="JobOutcome.IsTimedOut"/> with no value, Acta cancels the child and its subtree, and this Job resumes.
    /// </summary>
    public async Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(
        string name,
        TInput input,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        ToWaitTimeoutSeconds(timeout);
        var child = await StartChildAsync(name, input, configure, ct);
        var result = await TryWaitChildAsync(child.JobId, timeout, ct);
        if (result.Outcome is not { } outcome)
        {
            return JobOutcome<TResult>.TimedOut(child.JobId, TimedOutChildStatus);
        }
        if (outcome.Status != JobStatusCode.Succeeded)
        {
            return outcome.Status == JobStatusCode.Cancelled
                ? JobOutcome<TResult>.Cancelled(child.JobId)
                : JobOutcome<TResult>.Failed(child.JobId);
        }

        return JobOutcome<TResult>.Succeeded(child.JobId, await RequireChildResultAsync<TResult>(child.JobId, name, ct));
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="ExecuteChildAsync{TInput, TResult}(string, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the fourth positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(string name, TInput input, TimeSpan timeout, CancellationToken ct)
        where TInput : notnull
        where TResult : notnull => ExecuteChildAsync<TInput, TResult>(name, input, timeout, configure: null, ct: ct);

    private async Task<TResult> RequireChildResultAsync<TResult>(long childJobId, string name, CancellationToken ct)
        where TResult : notnull =>
        await GetChildResultAsync<TResult>(childJobId, ct)
        ?? throw new InvalidOperationException(
            $"Child job {childJobId} ('{name}') succeeded but stored no result; "
                + "use the non-result ExecuteChildAsync overload for result-less children."
        );

    private JobEnqueueOptions BuildChildOptions(string name, Action<JobEnqueueOptionsBuilder>? configure)
    {
        JobEnqueueOptions? configured = null;
        if (configure is not null)
        {
            var builder = new JobEnqueueOptionsBuilder();
            configure(builder);
            configured = builder.Build();
        }

        return new JobEnqueueOptions
        {
            JobNamespace = configured?.JobNamespace,
            DeduplicationKey = name,
            CorrelationKey = configured?.CorrelationKey,
            ConcurrencyKey = configured?.ConcurrencyKey,
            Lane = configured?.Lane,
            Priority = configured?.Priority,
            Tags = configured?.Tags,
            NextRunAtUtc = configured?.NextRunAtUtc,
            DelaySeconds = configured?.DelaySeconds,
            ParentJobId = JobId,
            TenantKey = configured?.TenantKey,
            OverrideParentTenant = configured?.OverrideParentTenant ?? false,
        };
    }

    /// <summary>
    /// Subclass sink: typed child enqueue through the shared enqueue path. The options carry the
    /// framework-set parent id and deduplication key.
    /// </summary>
    private protected abstract Task<EnqueueJobResponse> StartChildCoreAsync<TInput>(
        TInput input,
        JobEnqueueOptions options,
        CancellationToken ct
    )
        where TInput : notnull;

    /// <summary>
    /// Subclass sink: raw-route child enqueue through the shared enqueue path.
    /// </summary>
    private protected abstract Task<EnqueueJobResponse> StartChildCoreAsync(EnqueueJobRequest request, CancellationToken ct);

    /// <summary>
    /// Subclass sink: read and deserialize the child's stored result.
    /// </summary>
    private protected abstract Task<TResult?> GetChildResultCoreAsync<TResult>(long childJobId, CancellationToken ct);
}
