namespace Acta;

public abstract partial class JobContext
{
    /// <summary>
    /// Runs <paramref name="body"/> as a durable, replay-safe step named <paramref name="name"/> (kebab-case, unique
    /// per Job). The first pass runs the body and records the outcome; a replay of a succeeded step returns without
    /// re-running it; a failure retries with backoff, re-arming the parent budget-neutrally, until it succeeds or
    /// exhausts its budget and throws <see cref="StepExhaustedException"/>. The body must be idempotent: a crash after
    /// the side effect but before the record re-runs it on replay, and bare handler code between steps re-runs on
    /// every retry. <paramref name="configure"/> overrides the parent <c>[Job]</c> retry policy per step.
    /// </summary>
    public async Task RunStepAsync(
        string name,
        Func<CancellationToken, Task> body,
        Action<StepOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        ArgumentNullException.ThrowIfNull(body);
        var options = BuildStepOptions(configure);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await RunStepCoreAsync(name, body, options, linked.Token);
    }

    /// <summary>
    /// <see cref="RunStepAsync(string, Func{CancellationToken, Task}, Action{StepOptionsBuilder}, CancellationToken)"/>
    /// with <paramref name="ct"/> in the third slot: no <c>configure</c>, no named <c>ct: ct</c>.
    /// </summary>
    /// <param name="name">Stable kebab-case slot name, unique per Job; durable identity across replays.</param>
    /// <param name="body">The side-effecting work; receives a cancellation token linked to the attempt.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task RunStepAsync(string name, Func<CancellationToken, Task> body, CancellationToken ct) =>
        RunStepAsync(name, body, configure: null, ct: ct);

    /// <summary>
    /// Result-returning variant of
    /// <see cref="RunStepAsync(string, Func{CancellationToken, Task}, Action{StepOptionsBuilder}, CancellationToken)"/>:
    /// a replay of a succeeded step returns the stored result deserialized to <typeparamref name="TResult"/> without
    /// re-running the body, or throws <see cref="StepResultContractMismatchException"/> when it no longer fits. Wrap
    /// each outside-world effect in its own step, one effect per step: jobs are at-least-once, so an unwrapped call
    /// repeats on every retry and a crash between two effects in one step re-runs both.
    /// </summary>
    public async Task<TResult> RunStepAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        Action<StepOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        ArgumentNullException.ThrowIfNull(body);
        var options = BuildStepOptions(configure);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await RunStepCoreAsync(name, body, options, linked.Token);
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="RunStepAsync{TResult}(string, Func{CancellationToken, Task{TResult}}, Action{StepOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third positional slot when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<TResult> RunStepAsync<TResult>(string name, Func<CancellationToken, Task<TResult>> body, CancellationToken ct) =>
        RunStepAsync(name, body, configure: null, ct: ct);

    private static StepOptions BuildStepOptions(Action<StepOptionsBuilder>? configure)
    {
        if (configure is null)
        {
            return StepOptions.Inherit;
        }

        var builder = new StepOptionsBuilder();
        configure(builder);
        return builder.Build();
    }

    /// <summary>
    /// Subclass sink: runs the void step orchestration (start, body, complete, retry or exhaust).
    /// Returns normally on success; throws the framework step-retry signal to re-arm the Job, or
    /// <see cref="StepExhaustedException"/> when the budget is spent.
    /// </summary>
    private protected abstract Task RunStepCoreAsync(
        string name,
        Func<CancellationToken, Task> body,
        StepOptions options,
        CancellationToken ct
    );

    /// <summary>
    /// Subclass sink: result-returning step orchestration. Returns the (possibly replayed) result;
    /// throws as described on
    /// <see cref="RunStepCoreAsync(string, Func{CancellationToken, Task}, StepOptions, CancellationToken)"/>.
    /// </summary>
    private protected abstract Task<TResult> RunStepCoreAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        StepOptions options,
        CancellationToken ct
    );
}
