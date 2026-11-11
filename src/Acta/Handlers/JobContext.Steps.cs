namespace Acta;

public abstract partial class JobContext
{
    /// <summary>The at-most-once step whose body this async flow is running, if any.</summary>
    private readonly AsyncLocal<string?> _atMostOnceStep = new();

    /// <summary>
    /// Runs <paramref name="body"/> as a durable, replay-safe step named <paramref name="name"/> (kebab-case, unique
    /// per Job). The first pass runs the body and records the outcome; a replay of a succeeded step returns without
    /// re-running it. A body that throws fails the attempt, and the parent <c>[Job]</c>'s <c>MaxAttempts</c> and
    /// <c>Backoff</c> retry it: the retry replays the steps that succeeded and runs this one again. The body must be
    /// idempotent: a crash after the side effect but before the record re-runs it, and so does the resume after a wait
    /// inside the body. <paramref name="configure"/> chooses <see cref="StepOptionsBuilder.AtMostOnce"/>.
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
        var options = BuildStepOptions(name, configure);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await RunStepCoreAsync(name, body, options, linked.Token);
    }

    /// <summary>
    /// <see cref="RunStepAsync(string, Func{CancellationToken, Task}, Action{StepOptionsBuilder}, CancellationToken)"/>
    /// with <paramref name="ct"/> in the third position: no <c>configure</c>, no named <c>ct: ct</c>.
    /// </summary>
    /// <param name="name">Stable kebab-case step name, unique per Job; durable identity across replays.</param>
    /// <param name="body">The side-effecting work; receives a cancellation token linked to the attempt.</param>
    /// <param name="ct">Cancellation; linked with the per-attempt <see cref="CancellationToken"/>.</param>
    public Task RunStepAsync(string name, Func<CancellationToken, Task> body, CancellationToken ct) =>
        RunStepAsync(name, body, configure: null, ct: ct);

    /// <summary>
    /// Result-returning variant of
    /// <see cref="RunStepAsync(string, Func{CancellationToken, Task}, Action{StepOptionsBuilder}, CancellationToken)"/>:
    /// a replay of a succeeded step returns the stored result as <typeparamref name="TResult"/> without re-running the body,
    /// or throws <see cref="StepResultContractMismatchException"/> when it no longer fits. A result that is null, cannot be
    /// serialized, or is over the inline cap throws <see cref="StepExhaustedException"/>. Wrap each outside-world effect in
    /// its own step: an unwrapped call repeats on every retry, and a crash between two effects in one step re-runs both.
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
        var options = BuildStepOptions(name, configure);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await RunStepCoreAsync(name, body, options, linked.Token);
    }

    /// <summary>
    /// Convenience overload of
    /// <see cref="RunStepAsync{TResult}(string, Func{CancellationToken, Task{TResult}}, Action{StepOptionsBuilder}, CancellationToken)"/>:
    /// <paramref name="ct"/> takes the third position when no <c>configure</c> override is
    /// needed, instead of requiring the named <c>ct: ct</c> form. Equivalent to <c>configure: null</c>.
    /// </summary>
    public Task<TResult> RunStepAsync<TResult>(string name, Func<CancellationToken, Task<TResult>> body, CancellationToken ct) =>
        RunStepAsync(name, body, configure: null, ct: ct);

    /// <summary>
    /// The step's options; an at-most-once step marks the calling async flow, so a suspension anywhere in its body,
    /// nested steps included, is refused. The mark ends with the calling <c>RunStepAsync</c>.
    /// </summary>
    private StepOptions BuildStepOptions(string name, Action<StepOptionsBuilder>? configure)
    {
        if (configure is null)
        {
            return StepOptions.Inherit;
        }

        var builder = new StepOptionsBuilder();
        configure(builder);
        var options = builder.Build();
        if (options.AtMostOnce)
        {
            _atMostOnceStep.Value = name;
        }
        return options;
    }

    /// <summary>
    /// Refuses a call that can suspend the job when it runs inside an at-most-once step body, before the call writes
    /// anything: the resume would replay the job from the top, and the body never runs twice.
    /// </summary>
    private void ThrowIfInAtMostOnceStep(string call)
    {
        if (_atMostOnceStep.Value is { } step)
        {
            throw AtMostOnceSuspensionRefused(call, step);
        }
    }

    /// <summary>The refusal of a suspension inside the at-most-once step <paramref name="step"/>, called or thrown.</summary>
    private protected static InvalidOperationException AtMostOnceSuspensionRefused(string call, string step) =>
        new(
            $"{call} inside the AtMostOnce step '{step}' is refused: it can suspend the job, and resuming replays the "
                + "job from the top, which would run the step's body again. Move the wait out of the step."
        );

    /// <summary>
    /// Subclass sink: runs the void step orchestration (start, body, record). Returns normally on success; a body
    /// failure propagates, or for an at-most-once step becomes <see cref="StepExhaustedException"/>.
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
