using System.Diagnostics;

namespace Acta;

public abstract partial class JobContext
{
    /// <summary>The at-most-once or check step whose body this async flow is running, if any.</summary>
    private readonly AsyncLocal<(string Name, bool AtMostOnce)?> _markedStep = new();

    /// <summary>
    /// Runs <paramref name="body"/> as a durable, replay-safe step named <paramref name="name"/> (kebab-case, unique
    /// per Job). The first pass runs the body and records the outcome; a replay of a succeeded step returns without
    /// re-running it. A body that throws fails the attempt, and the parent <c>[Job]</c>'s <c>MaxAttempts</c> and
    /// <c>Backoff</c> retry it: the retry replays the steps that succeeded and runs this one again. The body must be
    /// idempotent: a crash after the side effect but before the record re-runs it, and so does the resume after a wait
    /// inside the body. <paramref name="configure"/> chooses <see cref="StepOptionsBuilder.AtMostOnce"/>.
    /// </summary>
    [DebuggerDisableUserUnhandledExceptions]
    public async Task RunStepAsync(
        string name,
        Func<CancellationToken, Task> body,
        Action<StepOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        ArgumentNullException.ThrowIfNull(body);
        var options = BuildStepOptions(name, configure);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await RunStepCoreAsync(name, body, options, check: null, linked.Token);
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
    /// or throws <see cref="StepResultContractMismatchException"/> when it no longer fits. A null result stores none and
    /// replays as null; one that cannot be serialized or stored throws <see cref="StepFailedException"/>. Wrap each
    /// outside-world effect in its own step: an unwrapped call repeats on retry, and a crash between two in one re-runs both.
    /// </summary>
    [DebuggerDisableUserUnhandledExceptions]
    public async Task<TResult> RunStepAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        Action<StepOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
    {
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        ArgumentNullException.ThrowIfNull(body);
        var options = BuildStepOptions(name, configure);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await RunStepCoreAsync(name, body, options, check: null, linked.Token);
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
    /// Runs <paramref name="body"/> as a durable step whose <paramref name="check"/> asks the outside world whether the body's
    /// effect already happened. The check is asked only when the step was started and has no recorded result, in the same
    /// attempt or a later one: never on a first start, and never after <c>ResetStateAsync</c>. A value it answers is recorded
    /// as the step's result and the body does not run; null runs the body. The check must change nothing and answer only for
    /// this step's own effect. A wait, sleep, reschedule, pause or child wait in the body fails the job at once.
    /// </summary>
    public Task<TResult> RunStepWithCheckAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        Func<CancellationToken, Task<TResult?>> check,
        CancellationToken ct = default
    )
        where TResult : class?
    {
        ArgumentNullException.ThrowIfNull(check);
        return RunCheckStepAsync(name, body, async token => await check(token) is { } found ? (true, found) : (false, default!), ct);
    }

    /// <inheritdoc cref="RunStepWithCheckAsync{TResult}(string, Func{CancellationToken, Task{TResult}}, Func{CancellationToken, Task{TResult}}, CancellationToken)"/>
    public Task<TResult> RunStepWithCheckAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        Func<CancellationToken, Task<TResult?>> check,
        CancellationToken ct = default
    )
        where TResult : struct
    {
        ArgumentNullException.ThrowIfNull(check);
        return RunCheckStepAsync(name, body, async token => await check(token) is { } found ? (true, found) : (false, default), ct);
    }

    /// <summary>
    /// Runs <paramref name="body"/>, which has no result, as a durable step whose <paramref name="check"/> asks the outside
    /// world whether its effect already happened. The check is asked only when the step was started and has no recorded
    /// result, in the same attempt or a later one: never on a first start, and never after <c>ResetStateAsync</c>. True records
    /// the step as succeeded and the body does not run; false runs the body. The check must change nothing and answer only
    /// for this step's own effect. A wait, sleep, reschedule, pause or child wait in the body fails the job at once.
    /// </summary>
    [DebuggerDisableUserUnhandledExceptions]
    public async Task RunStepWithCheckAsync(
        string name,
        Func<CancellationToken, Task> body,
        Func<CancellationToken, Task<bool>> check,
        CancellationToken ct = default
    )
    {
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(check);
        _markedStep.Value = (name, AtMostOnce: false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        await RunStepCoreAsync(name, body, StepOptions.Inherit, check, linked.Token);
    }

    /// <summary>A check step with a result. It marks the calling async flow, as an at-most-once step does.</summary>
    [DebuggerDisableUserUnhandledExceptions]
    private async Task<TResult> RunCheckStepAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        Func<CancellationToken, Task<(bool Found, TResult Value)>> check,
        CancellationToken ct
    )
    {
        name = NameSyntax.CanonicalizeDurable(name, nameof(name));
        ArgumentNullException.ThrowIfNull(body);
        _markedStep.Value = (name, AtMostOnce: false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, CancellationToken);
        return await RunStepCoreAsync(name, body, StepOptions.Inherit, check, linked.Token);
    }

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
            _markedStep.Value = (name, AtMostOnce: true);
        }
        return options;
    }

    /// <summary>
    /// Refuses a call that can suspend the job when it runs inside an at-most-once or check step body, before the call
    /// writes anything: the resume would replay the job from the top, where an at-most-once body never runs twice and a
    /// check step's check can find the body's effect and skip the rest of the body.
    /// </summary>
    private void ThrowIfInAtMostOnceStep(string call)
    {
        if (_markedStep.Value is { } step)
        {
            throw step.AtMostOnce ? AtMostOnceSuspensionRefused(call, step.Name) : (Exception)CheckStepSuspensionRefused(call, step.Name);
        }
    }

    /// <summary>The refusal of a suspension inside the at-most-once step <paramref name="step"/>, at the call or in the body.</summary>
    private protected static InvalidOperationException AtMostOnceSuspensionRefused(string call, string step) =>
        new(
            $"{call} inside the AtMostOnce step '{step}' is refused: it can suspend the job, and resuming replays the "
                + "job from the top, which would run the step's body again. Move the wait out of the step."
        );

    /// <summary>
    /// The refusal of a suspension inside the check step <paramref name="step"/>, at the call or in the body. It fails the job
    /// at once and leaves the step as it is, since a retry would ask the check, find the body's effect, and skip the rest.
    /// </summary>
    private protected static StepFailedException CheckStepSuspensionRefused(string call, string step) =>
        new(
            step,
            $"{call} inside a check step's body is refused: a resume would ask the check, which can find the body's effect and "
                + "skip the rest of the body. Move the wait between steps"
        );

    /// <summary>
    /// Subclass sink: runs the void step orchestration (start, check, body, record). Returns normally on success; a body
    /// failure propagates, or for an at-most-once step becomes <see cref="StepFailedException"/>. A
    /// <paramref name="check"/> is asked only when the step was started before and has no recorded result.
    /// </summary>
    private protected abstract Task RunStepCoreAsync(
        string name,
        Func<CancellationToken, Task> body,
        StepOptions options,
        Func<CancellationToken, Task<bool>>? check,
        CancellationToken ct
    );

    /// <summary>
    /// Subclass sink: result-returning step orchestration. Returns the (possibly replayed or found) result;
    /// throws as described on
    /// <see cref="RunStepCoreAsync(string, Func{CancellationToken, Task}, StepOptions, Func{CancellationToken, Task{bool}}, CancellationToken)"/>.
    /// </summary>
    private protected abstract Task<TResult> RunStepCoreAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        StepOptions options,
        Func<CancellationToken, Task<(bool Found, TResult Value)>>? check,
        CancellationToken ct
    );
}
