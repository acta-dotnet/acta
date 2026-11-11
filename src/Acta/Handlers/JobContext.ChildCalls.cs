namespace Acta;

// The child forms that take a generated call, as IJobs has them: the child is the job the call names, so nothing is
// inferred from the input's type, and a call of a job with a result carries the result type. Each mirrors the
// input-typed form of the same name and shape in JobContext.Children.cs.
public abstract partial class JobContext
{
    /// <summary>
    /// <see cref="StartChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/> for a
    /// generated call: <c>ctx.StartChildAsync("ship", ShippingJobs.Ship(order))</c>.
    /// </summary>
    public Task<EnqueueJobResponse> StartChildAsync<TInput>(
        string name,
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull =>
        StartChildCallAsync(name, job.Contract.ManifestType, job.Contract.JobName, job.Input, null, configure, ct);

    /// <inheritdoc cref="StartChildAsync{TInput}(string, JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<EnqueueJobResponse> StartChildAsync<TInput>(string name, JobCall<TInput> job, CancellationToken ct)
        where TInput : notnull => StartChildAsync(name, job, configure: null, ct: ct);

    /// <inheritdoc cref="StartChildAsync{TInput}(string, JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<EnqueueJobResponse> StartChildAsync<TInput, TResult>(
        string name,
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull =>
        StartChildCallAsync(name, job.Contract.ManifestType, job.Contract.JobName, job.Input, typeof(TResult), configure, ct);

    /// <inheritdoc cref="StartChildAsync{TInput}(string, JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<EnqueueJobResponse> StartChildAsync<TInput, TResult>(string name, JobCall<TInput, TResult> job, CancellationToken ct)
        where TInput : notnull
        where TResult : notnull => StartChildAsync(name, job, configure: null, ct: ct);

    /// <summary>
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/> for a
    /// generated call of a job with no result.
    /// </summary>
    public async Task<JobOutcome> ExecuteChildAsync<TInput>(
        string name,
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(ExecuteChildAsync));
        return await SettleChildAsync(await StartChildAsync(name, job, configure, ct), timeout: null, ct);
    }

    /// <inheritdoc cref="ExecuteChildAsync{TInput}(string, JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<JobOutcome> ExecuteChildAsync<TInput>(string name, JobCall<TInput> job, CancellationToken ct)
        where TInput : notnull => ExecuteChildAsync(name, job, configure: null, ct: ct);

    /// <summary>
    /// <see cref="ExecuteChildAsync{TInput}(string, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    /// for a generated call of a job with no result.
    /// </summary>
    public async Task<JobOutcome> ExecuteChildAsync<TInput>(
        string name,
        JobCall<TInput> job,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(ExecuteChildAsync));
        ToWaitTimeoutSeconds(timeout);
        return await SettleChildAsync(await StartChildAsync(name, job, configure, ct), timeout, ct);
    }

    /// <inheritdoc cref="ExecuteChildAsync{TInput}(string, JobCall{TInput}, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<JobOutcome> ExecuteChildAsync<TInput>(string name, JobCall<TInput> job, TimeSpan timeout, CancellationToken ct)
        where TInput : notnull => ExecuteChildAsync(name, job, timeout, configure: null, ct: ct);

    /// <summary>
    /// <see cref="ExecuteChildAsync{TInput, TResult}(string, TInput, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    /// for a generated call, which names the result type: <c>ctx.ExecuteChildAsync("ship", ShippingJobs.Ship(order), ct)</c>
    /// answers the child's outcome with its result.
    /// </summary>
    public async Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(
        string name,
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(ExecuteChildAsync));
        return await SettleChildAsync<TResult>(await StartChildAsync(name, job, configure, ct), name, timeout: null, ct);
    }

    /// <inheritdoc cref="ExecuteChildAsync{TInput, TResult}(string, JobCall{TInput, TResult}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(string name, JobCall<TInput, TResult> job, CancellationToken ct)
        where TInput : notnull
        where TResult : notnull => ExecuteChildAsync(name, job, configure: null, ct: ct);

    /// <summary>
    /// <see cref="ExecuteChildAsync{TInput, TResult}(string, TInput, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    /// for a generated call, which names the result type.
    /// </summary>
    public async Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(
        string name,
        JobCall<TInput, TResult> job,
        TimeSpan timeout,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        ThrowIfInAtMostOnceStep(nameof(ExecuteChildAsync));
        ToWaitTimeoutSeconds(timeout);
        return await SettleChildAsync<TResult>(await StartChildAsync(name, job, configure, ct), name, timeout, ct);
    }

    /// <inheritdoc cref="ExecuteChildAsync{TInput, TResult}(string, JobCall{TInput, TResult}, TimeSpan, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>
    public Task<JobOutcome<TResult>> ExecuteChildAsync<TInput, TResult>(
        string name,
        JobCall<TInput, TResult> job,
        TimeSpan timeout,
        CancellationToken ct
    )
        where TInput : notnull
        where TResult : notnull => ExecuteChildAsync(name, job, timeout, configure: null, ct: ct);

    private async Task<EnqueueJobResponse> StartChildCallAsync<TInput>(
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
        return await StartChildCallCoreAsync(manifestType, jobName, input, resultType, BuildChildOptions(name, configure), linked.Token);
    }

    /// <summary>
    /// Subclass sink: child enqueue of a generated call, routed by the job the call names; a
    /// <paramref name="resultType"/> is checked against the job's own.
    /// </summary>
    private protected abstract Task<EnqueueJobResponse> StartChildCallCoreAsync<TInput>(
        Type? manifestType,
        string? jobName,
        TInput input,
        Type? resultType,
        JobEnqueueOptions options,
        CancellationToken ct
    )
        where TInput : notnull;
}
