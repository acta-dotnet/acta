namespace Acta;

/// <summary>
/// Fluent override builder handed to the <c>ctx.RunStepAsync</c> <c>configure</c> callback: each setter records one
/// partial override, unset fields inherit the parent <c>[Job]</c> retry policy, validation is eager at the setter,
/// and the last write per field wins. Durations take <see cref="TimeSpan"/>, rounding sub-second values up to whole
/// seconds, the persisted precision. The framework constructs the builder, runs the callback, then snapshots it via
/// <see cref="Build"/>; callers never construct it or call <see cref="Build"/>.
/// </summary>
public sealed class StepOptionsBuilder
{
    private int? _maxAttempts;
    private int? _backoffInitialDelaySeconds;
    private int? _backoffMaxDelaySeconds;
    private decimal? _backoffMultiplier;
    private decimal? _backoffJitter;
    private int? _retryWindowSeconds;
    private bool _atMostOnce;

    internal StepOptionsBuilder() { }

    /// <summary>
    /// Override the maximum number of attempts before the step exhausts; at least 1.
    /// </summary>
    public StepOptionsBuilder MaxAttempts(int maxAttempts)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "MaxAttempts must be at least 1.");
        }
        _maxAttempts = maxAttempts;
        return this;
    }

    /// <summary>
    /// Override the whole retry backoff policy from Acta's DSL, e.g. <c>"1m..8h x2 +-10%"</c>.
    /// </summary>
    public StepOptionsBuilder Backoff(string expression) => WithPolicy(Acta.Backoff.Parse(expression));

    /// <summary>
    /// Override the whole retry backoff policy at once from a typed <see cref="Acta.Backoff"/>, setting
    /// the initial delay, max delay, multiplier, and jitter together.
    /// </summary>
    public StepOptionsBuilder WithPolicy(Backoff backoff)
    {
        BackoffInitialDelay(backoff.InitialDelay);
        BackoffMaxDelay(backoff.MaxDelay);
        BackoffMultiplier(backoff.Multiplier);
        BackoffJitter(backoff.Jitter);
        return this;
    }

    /// <summary>Override the initial retry backoff delay.</summary>
    public StepOptionsBuilder BackoffInitialDelay(TimeSpan delay)
    {
        _backoffInitialDelaySeconds = DurationSyntax.ToWholeSeconds(delay, nameof(delay));
        return this;
    }

    /// <summary>Override the maximum retry backoff delay (the growth-curve cap).</summary>
    public StepOptionsBuilder BackoffMaxDelay(TimeSpan delay)
    {
        _backoffMaxDelaySeconds = DurationSyntax.ToWholeSeconds(delay, nameof(delay));
        return this;
    }

    /// <summary>Override the backoff growth multiplier applied per failed attempt (at least 1).</summary>
    public StepOptionsBuilder BackoffMultiplier(double multiplier)
    {
        if (multiplier < 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "BackoffMultiplier must be at least 1.0.");
        }
        _backoffMultiplier = (decimal)multiplier;
        return this;
    }

    /// <summary>Override the symmetric backoff jitter fraction in <c>[0, 1]</c>.</summary>
    public StepOptionsBuilder BackoffJitter(double jitter)
    {
        if (jitter is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), jitter, "BackoffJitter must be in [0, 1].");
        }
        _backoffJitter = (decimal)jitter;
        return this;
    }

    /// <summary>
    /// Override the retry window: the step exhausts once a computed next retry would fall beyond
    /// <c>created_at + window</c>, independent of <see cref="MaxAttempts"/>. Unset means no window.
    /// </summary>
    public StepOptionsBuilder RetryWindow(TimeSpan window)
    {
        _retryWindowSeconds = DurationSyntax.ToWholeSeconds(window, nameof(window));
        return this;
    }

    /// <summary>
    /// Runs the step body at most once: zero or one invocation, never retried, for non-idempotent effects (charge a
    /// card, send an email) where a double execution is worse than a skipped one. If the worker dies after the step
    /// start is recorded but before the outcome, replay does not re-run the body: the step becomes terminal
    /// <c>Interrupted</c>, <c>ctx.RunStepAsync</c> throws <see cref="StepInterruptedException"/>, and the handler
    /// reconciles against the external system. Any retry setting other than <c>MaxAttempts(1)</c> makes <see cref="Build"/>
    /// throw, in either order; the policy is read from handler code on replay, so switching it mid-flight reinterprets pending steps.
    /// </summary>
    public StepOptionsBuilder AtMostOnce()
    {
        _atMostOnce = true;
        return this;
    }

    /// <summary>
    /// Snapshots the recorded overrides into an immutable <see cref="StepOptions"/>. Repeatable.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="AtMostOnce"/> was combined with a retry override other than <c>MaxAttempts(1)</c>.
    /// </exception>
    internal StepOptions Build()
    {
        if (_atMostOnce)
        {
            // AtMostOnce means the body runs at most once, so any retry configuration contradicts it. The
            // only permitted retry override is an explicit MaxAttempts(1); everything else is rejected in
            // either call order (validation is here, not at the setters, because call order is arbitrary).
            if (_maxAttempts is not (null or 1))
            {
                throw new ArgumentException(
                    $"AtMostOnce() forbids retries, so MaxAttempts({_maxAttempts}) is invalid; only MaxAttempts(1) is allowed."
                );
            }
            if (
                _backoffInitialDelaySeconds is not null
                || _backoffMaxDelaySeconds is not null
                || _backoffMultiplier is not null
                || _backoffJitter is not null
            )
            {
                throw new ArgumentException("AtMostOnce() forbids retries, so Backoff(...) overrides are invalid.");
            }
            if (_retryWindowSeconds is not null)
            {
                throw new ArgumentException("AtMostOnce() forbids retries, so RetryWindow(...) is invalid.");
            }
        }

        return new StepOptions(
            _maxAttempts,
            _backoffInitialDelaySeconds,
            _backoffMaxDelaySeconds,
            _backoffMultiplier,
            _backoffJitter,
            _retryWindowSeconds,
            _atMostOnce
        );
    }
}
