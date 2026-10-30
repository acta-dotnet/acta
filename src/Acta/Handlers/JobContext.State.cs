namespace Acta;

public abstract partial class JobContext
{
    /// <summary>
    /// Reports progress as a typed value, JSON-serialized into the reserved <c>sys.progress</c> checkpoint slot. Last
    /// write wins. Silent: no event, no version bump.
    /// </summary>
    public async Task SetProgressAsync<T>(T value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SetProgressCoreAsync(value, linked.Token);
    }

    /// <summary>
    /// Subclass sink for <see cref="SetProgressAsync{T}"/>. The subclass serializes through its payload
    /// serializer (so source-gen JSON is honored under Native AOT) rather than the reflection-based
    /// static helper.
    /// </summary>
    private protected abstract Task SetProgressCoreAsync<T>(T value, CancellationToken ct);

    /// <summary>
    /// Set a durable per-job variable to a non-null JSON value. Last write wins.
    /// <para>
    /// Use this rather than a field or a static: a job can be resumed by a different process, so
    /// state that must survive a retry or a crash belongs in the ledger, not in memory.
    /// </para>
    /// </summary>
    public async Task SetVariableAsync<T>(string name, T value, CancellationToken ct = default)
        where T : notnull
    {
        name = ValidateUserVariableName(name);
        ArgumentNullException.ThrowIfNull(value);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SetVariableCoreAsync(name, value, linked.Token);
    }

    /// <summary>
    /// Set a durable per-job variable using an exact payload format and byte buffer.
    /// </summary>
    public async Task SetVariableAsync(string name, JobPayload payload, CancellationToken ct = default)
    {
        name = ValidateUserVariableName(name);
        if (payload.IsNone)
        {
            throw new ArgumentException("Variable payload cannot be None.", nameof(payload));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await SetVariableCoreAsync(name, payload, linked.Token);
    }

    /// <summary>
    /// Get a durable per-job variable, or <c>null</c> when the variable is absent.
    /// </summary>
    public async Task<T?> GetVariableOrDefaultAsync<T>(string name, CancellationToken ct = default)
    {
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var (found, value) = await TryGetVariableCoreAsync<T>(name, linked.Token);
        return found ? value : default;
    }

    /// <summary>
    /// Get a durable per-job variable, or <paramref name="defaultValue"/> when the variable is absent.
    /// </summary>
    public async Task<T> GetVariableOrDefaultAsync<T>(string name, T defaultValue, CancellationToken ct = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        // One read that reports existence: a value-type T cannot carry "absent" as null, so a stored
        // value-type default (e.g. 0) must not be mistaken for an absent variable.
        var (found, value) = await TryGetVariableCoreAsync<T>(name, linked.Token);
        return found ? value! : defaultValue;
    }

    /// <summary>
    /// Get a durable per-job variable, or throw when the variable is absent.
    /// </summary>
    public async Task<T> GetRequiredVariableAsync<T>(string name, CancellationToken ct = default)
        where T : notnull
    {
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        var (found, value) = await TryGetVariableCoreAsync<T>(name, linked.Token);
        return found ? value! : throw new InvalidOperationException($"Job variable '{name}' does not exist.");
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="GetOrSetVariableAsync{T}(string, Func{CancellationToken, Task{T}}, CancellationToken)"/>
    /// taking a synchronous factory.
    /// </summary>
    public Task<T> GetOrSetVariableAsync<T>(string name, Func<T> valueFactory, CancellationToken ct = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        return GetOrSetVariableAsync(name, _ => Task.FromResult(valueFactory()), ct);
    }

    /// <summary>
    /// Get a durable per-job variable, or compute and store a non-null JSON value when absent.
    /// </summary>
    public async Task<T> GetOrSetVariableAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct = default
    )
        where T : notnull
    {
        name = ValidateUserVariableName(name);
        ArgumentNullException.ThrowIfNull(valueFactory);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await GetOrSetVariableCoreAsync(name, valueFactory, linked.Token);
    }

    /// <summary>
    /// True when a durable per-job variable exists.
    /// </summary>
    public async Task<bool> ExistsVariableAsync(string name, CancellationToken ct = default)
    {
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await ExistsVariableCoreAsync(name, linked.Token);
    }

    /// <summary>
    /// Delete a durable per-job variable. Returns <c>true</c> when a row was removed.
    /// </summary>
    public async Task<bool> DeleteVariableAsync(string name, CancellationToken ct = default)
    {
        name = ValidateUserVariableName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await DeleteVariableCoreAsync(name, linked.Token);
    }

    private static string ValidateUserVariableName(string name) =>
        NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);

    /// <summary>
    /// Subclass sink: set a user variable using the default JSON format.
    /// </summary>
    private protected abstract Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct)
        where T : notnull;

    /// <summary>
    /// Subclass sink: set a user variable using an exact payload.
    /// </summary>
    private protected abstract Task SetVariableCoreAsync(string name, JobPayload payload, CancellationToken ct);

    /// <summary>
    /// Subclass sink: get a user variable in one read, reporting whether it was found. The found flag is
    /// what distinguishes an absent variable from a stored value-type default (e.g. <c>0</c> for int).
    /// </summary>
    private protected abstract Task<(bool Found, T? Value)> TryGetVariableCoreAsync<T>(string name, CancellationToken ct);

    /// <summary>
    /// Subclass sink: get a user variable, or compute and store it when absent.
    /// </summary>
    private protected abstract Task<T> GetOrSetVariableCoreAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct
    )
        where T : notnull;

    /// <summary>
    /// Subclass sink: check whether a user variable exists.
    /// </summary>
    private protected abstract Task<bool> ExistsVariableCoreAsync(string name, CancellationToken ct);

    /// <summary>
    /// Subclass sink: delete a user variable.
    /// </summary>
    private protected abstract Task<bool> DeleteVariableCoreAsync(string name, CancellationToken ct);

    /// <summary>
    /// Clears this Job's durable state (every step, result row, and checkpoint other than the scheduler's firing
    /// envelope) so the next execution starts as new; the status, lease, failure budget, and schedule stay, the current attempt
    /// completes normally, and <c>job.state-reset</c> is emitted. Meant as the final action of a handler that runs
    /// again, never its first: mid-handler it discards the variables, timers, step checkpoints, and signals the rest of
    /// the attempt would read, a finished child still dedupes by name and never re-raises (so waiting on it again
    /// hangs; use per-fire child names), and an already-run <c>AtMostOnce</c> step can run twice if the attempt aborts.
    /// </summary>
    public async Task ResetStateAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await ResetStateCoreAsync(linked.Token);
    }

    /// <summary>
    /// Subclass sink: clear the Job's durable state (variables, timers, steps, signals, results).
    /// </summary>
    private protected abstract Task ResetStateCoreAsync(CancellationToken ct);
}
