using System.Diagnostics;

namespace Acta;

public abstract partial class JobContext
{
    private const string ChildSignalPrefix = "sys.child.";

    /// <summary>
    /// Enqueues a child job from a typed input, replay-safe: <paramref name="name"/> is the child's deduplication key,
    /// so a replay returns the existing child (<see cref="EnqueueJobAction.Deduplicated"/>); wait for it with <c>WaitChildAsync</c>.
    /// </summary>
    /// <param name="name">Stable kebab-case child name, unique among this Job's children.</param>
    /// <param name="input">Typed input resolved to a job route via the generated manifest.</param>
    /// <param name="configure">Optional enqueue options; the parent id and deduplication key are framework-set and win.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    [DebuggerDisableUserUnhandledExceptions]
    public async Task<EnqueueJobResponse> EnqueueChildAsync<TInput>(
        string name,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        ArgumentNullException.ThrowIfNull(input);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await EnqueueChildCoreAsync(input, BuildChildOptions(name, configure), linked.Token);
    }

    /// <summary>
    /// <see cref="EnqueueChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/> with
    /// <paramref name="ct"/> in the third position: no <c>configure</c>, no named <c>ct: ct</c>.
    /// </summary>
    /// <param name="name">Stable kebab-case child name, unique among this Job's children.</param>
    /// <param name="input">Typed input resolved to a job route via the generated manifest.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task<EnqueueJobResponse> EnqueueChildAsync<TInput>(string name, TInput input, CancellationToken ct)
        where TInput : notnull => EnqueueChildAsync(name, input, configure: null, ct: ct);

    /// <summary>
    /// Raw-route variant of
    /// <see cref="EnqueueChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    /// for explicit
    /// (<paramref name="jobNamespace"/>, <paramref name="jobName"/>) targeting, including a namespace
    /// other than this Job's.
    /// </summary>
    [DebuggerDisableUserUnhandledExceptions]
    public async Task<EnqueueJobResponse> EnqueueChildAsync(
        string name,
        string jobNamespace,
        string jobName,
        JobPayload input = default,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
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
            TenantName: options.TenantName,
            OverrideParentTenant: options.OverrideParentTenant,
            Lane: options.Lane
        );

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await EnqueueChildCoreAsync(request, linked.Token);
    }

    /// <summary>
    /// A child's stored result, deserialized to <typeparamref name="TResult"/>, for a handler that expects one: read it
    /// after a wait answered the child Succeeded. Throws <see cref="InvalidOperationException"/> when the child has
    /// stored none; <see cref="TryGetChildResultAsync{TResult}"/> reads one that may not exist, and states the rest.
    /// </summary>
    public async Task<TResult> GetChildResultAsync<TResult>(long childJobId, CancellationToken ct = default)
        where TResult : notnull =>
        await TryGetChildResultAsync<TResult>(childJobId, ct) is (true, var value)
            ? value!
            : throw new InvalidOperationException(
                $"Child job {childJobId} has stored no result: it has not succeeded, it returns none, or retention "
                    + "purged it. TryGetChildResultAsync reads a result that may not exist."
            );

    /// <summary>
    /// Reads a child's stored result, deserialized to <typeparamref name="TResult"/>, and whether it has one:
    /// <c>Found</c> is false when the child stored none or was already purged by retention, which keeps a completed
    /// child while this job is not terminal. Point-in-time and non-blocking; wait for the child first when ordering
    /// matters.
    /// </summary>
    public async Task<(bool Found, TResult? Value)> TryGetChildResultAsync<TResult>(long childJobId, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childJobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await ReadChildResultCoreAsync<TResult>(childJobId, executionNumber: null, linked.Token);
    }

    /// <summary>
    /// Enqueues the named child and durably waits for its terminal outcome in one call, the child-level
    /// mirror of <c>IJobs.CallAsync</c>. Returned, never thrown; branch on the outcome or call
    /// <see cref="JobOutcome.ThrowIfFailed"/>. Waits this one child before returning: to fan out, enqueue
    /// every child first and wait for them with
    /// <see cref="WaitChildrenAsync(IReadOnlyList{EnqueueJobResponse}, CancellationToken)"/>.
    /// </summary>
    public async Task<JobOutcome> CallChildAsync<TInput>(
        string name,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(CallChildAsync));
        return await SettleChildAsync(await EnqueueChildAsync(name, input, configure, ct), timeout: null, ct);
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="CallChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third position when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome> CallChildAsync<TInput>(string name, TInput input, CancellationToken ct)
        where TInput : notnull => CallChildAsync(name, input, configure: null, ct: ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="CallChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// the child is started, then awaited under one stored expiration (<c>db_now + timeout</c>, whole seconds,
    /// positive) that a replay reuses and never extends. On expiry the outcome reports
    /// <see cref="JobOutcome.TimedOut"/>, Acta cancels the child and its descendant subtree, and this Job resumes.
    /// </summary>
    public async Task<JobOutcome> CallChildAsync<TInput>(
        string name,
        TInput input,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        // Checked before the child is started, not when the wait reaches it: a rejected call must not
        // leave an enqueued child behind that nothing is going to wait for.
        ThrowIfInAtMostOnceStep(nameof(CallChildAsync));
        ToWaitTimeoutSeconds(timeout);
        var child = await EnqueueChildAsync(name, input, configure, ct);
        // One child needs no group deadline checkpoint: the latch's own stored expiration already is the one
        // persisted absolute instant, and a second copy of it could only drift.
        return await SettleChildAsync(child, timeout, ct);
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="CallChildAsync{TInput}(string, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the fourth position when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<JobOutcome> CallChildAsync<TInput>(string name, TInput input, TimeSpan timeout, CancellationToken ct)
        where TInput : notnull => CallChildAsync(name, input, timeout, configure: null, ct: ct);

    /// <summary>Waits a started child to its outcome, under <paramref name="timeout"/> when one is given.</summary>
    private async Task<JobOutcome> SettleChildAsync(EnqueueJobResponse child, TimeSpan? timeout, CancellationToken ct) =>
        (await WaitChildCoreAsync(child, timeout, ct)).Outcome;

    /// <summary>
    /// <see cref="SettleChildAsync(EnqueueJobResponse, TimeSpan?, CancellationToken)"/> with the result a succeeded
    /// child stored; a success with none throws, since the caller asked for a result the child did not produce.
    /// </summary>
    private async Task<JobOutcome<TResult>> SettleChildAsync<TResult>(
        EnqueueJobResponse child,
        string name,
        TimeSpan? timeout,
        CancellationToken ct
    )
        where TResult : notnull => await LandAsync<TResult>(child, await WaitChildCoreAsync(child, timeout, ct), name, ct);

    /// <summary>A waited child's outcome with the result the execution its latch names stored, on a success.</summary>
    private async Task<JobOutcome<TResult>> LandAsync<TResult>(
        EnqueueJobResponse child,
        (JobOutcome Outcome, long? ExecutionNumber) wait,
        string name,
        CancellationToken ct
    )
        where TResult : notnull
    {
        if (wait.Outcome.TimedOut)
        {
            return JobOutcome<TResult>.Expired(child.JobId, child.JobRef, wait.Outcome.Status);
        }

        return wait.Outcome.Succeeded
            ? JobOutcome<TResult>.Landed(
                child.JobId,
                child.JobRef,
                await RequireChildResultAsync<TResult>(child, wait.ExecutionNumber, name, ct)
            )
            : JobOutcome<TResult>.Landed(child.JobId, child.JobRef, wait.Outcome.Status);
    }

    /// <summary>
    /// The result stored by the execution whose landing set the latch, or by the latest execution when the latch does
    /// not name one. An execution that stored none throws rather than answering another one's or a default; so does a
    /// child an operator restarted after it succeeded, since the restart clears the stored result, and this job's retry
    /// then reads the child's next landing.
    /// </summary>
    private async Task<TResult> RequireChildResultAsync<TResult>(
        EnqueueJobResponse child,
        long? executionNumber,
        string name,
        CancellationToken ct
    )
        where TResult : notnull
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var (stored, value) = await ReadChildResultCoreAsync<TResult>(child.JobId, executionNumber, linked.Token);
        return stored
            ? value!
            : throw new InvalidOperationException(
                $"Child job {child.JobRef} ('{name}') succeeded but stored no result: its result exceeded "
                    + "MaxInlinePayloadBytes and was dropped, or an operator restarted it after it succeeded; the child's "
                    + "events say which."
            );
    }

    /// <summary>
    /// Enqueues a fan-out group's child under <paramref name="key"/>, <c>{group}/{item}</c>, with the group's options. No
    /// child name holds a <c>/</c>, so a group's children share a key with no other group's and with no child the handler
    /// named itself.
    /// </summary>
    internal async Task<EnqueueJobResponse> EnqueueGroupChildAsync<TInput>(
        string key,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    )
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await EnqueueChildCoreAsync(input, BuildChildOptions(key, configure), linked.Token);
    }

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
            TenantName = configured?.TenantName,
            OverrideParentTenant = configured?.OverrideParentTenant ?? false,
        };
    }

    /// <summary>
    /// Subclass sink: typed child enqueue through the shared enqueue path. The options carry the
    /// framework-set parent id and deduplication key.
    /// </summary>
    private protected abstract Task<EnqueueJobResponse> EnqueueChildCoreAsync<TInput>(
        TInput input,
        JobEnqueueOptions options,
        CancellationToken ct
    )
        where TInput : notnull;

    /// <summary>
    /// Subclass sink: raw-route child enqueue through the shared enqueue path.
    /// </summary>
    private protected abstract Task<EnqueueJobResponse> EnqueueChildCoreAsync(EnqueueJobRequest request, CancellationToken ct);

    /// <summary>
    /// Subclass sink: read and deserialize the result <paramref name="executionNumber"/> of the child stored, or its
    /// latest execution's when null. <c>Stored</c> is false when that execution has no result row.
    /// </summary>
    private protected abstract Task<(bool Stored, TResult? Value)> ReadChildResultCoreAsync<TResult>(
        long childJobId,
        long? executionNumber,
        CancellationToken ct
    );
}
