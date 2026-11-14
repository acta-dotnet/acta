using System.Diagnostics;

namespace Acta;

// The child forms that take a generated call, as IJobs has them: the child is the job the call names, so nothing is
// inferred from the input's type, and a call of a job with a result carries the result type. Each mirrors the
// input-typed form of the same name and shape in JobContext.Children.cs.
public abstract partial class JobContext
{
    /// <summary>
    /// <see cref="EnqueueChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/> for a
    /// generated call: <c>ctx.EnqueueChildAsync("ship", ShippingJobs.Ship(order))</c>.
    /// </summary>
    public Task<EnqueueJobResponse> EnqueueChildAsync<TInput>(
        string name,
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull =>
        EnqueueChildCallAsync(name, job.Contract.ManifestType, job.Contract.JobName, job.Input, null, configure, ct);

    /// <inheritdoc cref="EnqueueChildAsync{TInput}(string, JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<EnqueueJobResponse> EnqueueChildAsync<TInput>(string name, JobCall<TInput> job, CancellationToken ct)
        where TInput : notnull => EnqueueChildAsync(name, job, configure: null, ct: ct);

    /// <inheritdoc cref="EnqueueChildAsync{TInput}(string, JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<EnqueueJobResponse> EnqueueChildAsync<TInput, TResult>(
        string name,
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull =>
        EnqueueChildCallAsync(name, job.Contract.ManifestType, job.Contract.JobName, job.Input, typeof(TResult), configure, ct);

    /// <inheritdoc cref="EnqueueChildAsync{TInput}(string, JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<EnqueueJobResponse> EnqueueChildAsync<TInput, TResult>(string name, JobCall<TInput, TResult> job, CancellationToken ct)
        where TInput : notnull
        where TResult : notnull => EnqueueChildAsync(name, job, configure: null, ct: ct);

    /// <summary>
    /// <see cref="CallChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/> for a
    /// generated call of a job with no result.
    /// </summary>
    public async Task<JobOutcome> CallChildAsync<TInput>(
        string name,
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(CallChildAsync));
        return await SettleChildAsync(await EnqueueChildAsync(name, job, configure, ct), timeout: null, ct);
    }

    /// <inheritdoc cref="CallChildAsync{TInput}(string, JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<JobOutcome> CallChildAsync<TInput>(string name, JobCall<TInput> job, CancellationToken ct)
        where TInput : notnull => CallChildAsync(name, job, configure: null, ct: ct);

    /// <summary>
    /// <see cref="CallChildAsync{TInput}(string, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    /// for a generated call of a job with no result.
    /// </summary>
    public async Task<JobOutcome> CallChildAsync<TInput>(
        string name,
        JobCall<TInput> job,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(CallChildAsync));
        ToWaitTimeoutSeconds(timeout);
        return await SettleChildAsync(await EnqueueChildAsync(name, job, configure, ct), timeout, ct);
    }

    /// <inheritdoc cref="CallChildAsync{TInput}(string, JobCall{TInput}, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<JobOutcome> CallChildAsync<TInput>(string name, JobCall<TInput> job, TimeSpan timeout, CancellationToken ct)
        where TInput : notnull => CallChildAsync(name, job, timeout, configure: null, ct: ct);

    /// <summary>
    /// Enqueues the child a generated call names and waits for its outcome, typed by the job's result:
    /// <c>ctx.CallChildAsync("ship", ShippingJobs.Ship(order), ct)</c> answers the child's outcome with its result, read
    /// from the execution the wait saw.
    /// </summary>
    public async Task<JobOutcome<TResult>> CallChildAsync<TInput, TResult>(
        string name,
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(CallChildAsync));
        return await SettleChildAsync<TResult>(await EnqueueChildAsync(name, job, configure, ct), name, timeout: null, ct);
    }

    /// <inheritdoc cref="CallChildAsync{TInput, TResult}(string, JobCall{TInput, TResult}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<JobOutcome<TResult>> CallChildAsync<TInput, TResult>(string name, JobCall<TInput, TResult> job, CancellationToken ct)
        where TInput : notnull
        where TResult : notnull => CallChildAsync(name, job, configure: null, ct: ct);

    /// <summary>
    /// Bounded twin of
    /// <see cref="CallChildAsync{TInput, TResult}(string, JobCall{TInput, TResult}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>:
    /// on expiry the outcome reports <see cref="JobOutcome.TimedOut"/> with no value, and Acta cancels the child.
    /// </summary>
    public async Task<JobOutcome<TResult>> CallChildAsync<TInput, TResult>(
        string name,
        JobCall<TInput, TResult> job,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(CallChildAsync));
        ToWaitTimeoutSeconds(timeout);
        return await SettleChildAsync<TResult>(await EnqueueChildAsync(name, job, configure, ct), name, timeout, ct);
    }

    /// <inheritdoc cref="CallChildAsync{TInput, TResult}(string, JobCall{TInput, TResult}, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<JobOutcome<TResult>> CallChildAsync<TInput, TResult>(
        string name,
        JobCall<TInput, TResult> job,
        TimeSpan timeout,
        CancellationToken ct
    )
        where TInput : notnull
        where TResult : notnull => CallChildAsync(name, job, timeout, configure: null, ct: ct);

    [DebuggerDisableUserUnhandledExceptions]
    private async Task<EnqueueJobResponse> EnqueueChildCallAsync<TInput>(
        string name,
        Type? manifestType,
        string? jobName,
        TInput input,
        Type? resultType,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    )
        where TInput : notnull
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        ArgumentNullException.ThrowIfNull(input);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await EnqueueChildCallCoreAsync(manifestType, jobName, input, resultType, BuildChildOptions(name, configure), linked.Token);
    }

    /// <summary>
    /// Subclass sink: child enqueue of a generated call, routed by the job the call names; a
    /// <paramref name="resultType"/> is checked against the job's own.
    /// </summary>
    private protected abstract Task<EnqueueJobResponse> EnqueueChildCallCoreAsync<TInput>(
        Type? manifestType,
        string? jobName,
        TInput input,
        Type? resultType,
        JobEnqueueOptions options,
        CancellationToken ct
    )
        where TInput : notnull;
}
