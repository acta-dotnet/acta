using Acta.Runtime.Kernel;
using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Backoff value type: factory invariants, jitter clamping, and faithful projection through the public
/// ctx.RunStepAsync override path into the StepOptions scalars the engine persists.
/// </summary>
public sealed class BackoffTests
{
    [Fact]
    public void Fixed_uses_a_flat_curve()
    {
        var backoff = Backoff.Fixed(TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), backoff.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), backoff.MaxDelay);
        Assert.Equal(1.0, backoff.Multiplier);
        Assert.Equal(0.0, backoff.Jitter);
    }

    [Fact]
    public void Exponential_defaults_to_doubling()
    {
        var backoff = Backoff.Exponential(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8));

        Assert.Equal(TimeSpan.FromMinutes(1), backoff.InitialDelay);
        Assert.Equal(TimeSpan.FromHours(8), backoff.MaxDelay);
        Assert.Equal(2.0, backoff.Multiplier);
    }

    [Fact]
    public void Default_is_one_minute_to_one_day_doubling_with_jitter()
    {
        var backoff = Backoff.Default;

        Assert.Equal(TimeSpan.FromMinutes(1), backoff.InitialDelay);
        Assert.Equal(TimeSpan.FromDays(1), backoff.MaxDelay);
        Assert.Equal(2.0, backoff.Multiplier);
        Assert.Equal(0.1, backoff.Jitter);
    }

    [Fact]
    public void DefaultExpression_parses_to_the_same_default_backoff()
    {
        // The registration/entity canonical default string must parse to exactly Backoff.Default, in both
        // the bare ranged form and the spelled-out form the framework now registers:
        // ranged expressions default multiplier 2.0 / jitter 0.1, matching Default's own construction.
        Assert.Equal(Backoff.Default, Backoff.Parse("1m..1d"));
        Assert.Equal(Backoff.Default, Backoff.Parse("1m..1d x2 ~10%"));
    }

    [Theory]
    [InlineData("30s", 30, 30, 1.0, 0.0, "30s")]
    [InlineData("1m..8h", 60, 28800, 2.0, 0.1, "1m..8h x2 ±10%")]
    [InlineData("1m..8h x3", 60, 28800, 3.0, 0.1, "1m..8h x3 ±10%")]
    [InlineData("1m..8h x2 ±20%", 60, 28800, 2.0, 0.2, "1m..8h x2 ±20%")]
    [InlineData("1m..8h exact", 60, 28800, 2.0, 0.0, "1m..8h x2 exact")]
    [InlineData("PT1M..PT8H x2 ~10%", 60, 28800, 2.0, 0.1, "1m..8h x2 ±10%")]
    [InlineData("30d..90d", 2592000, 7776000, 2.0, 0.1, "30d..90d x2 ±10%")]
    public void Parse_reads_the_backoff_dsl(
        string expression,
        int initialSeconds,
        int maxSeconds,
        double multiplier,
        double jitter,
        string canonical
    )
    {
        var backoff = Backoff.Parse(expression);

        Assert.Equal(TimeSpan.FromSeconds(initialSeconds), backoff.InitialDelay);
        Assert.Equal(TimeSpan.FromSeconds(maxSeconds), backoff.MaxDelay);
        Assert.Equal(multiplier, backoff.Multiplier);
        Assert.Equal(jitter, backoff.Jitter);
        Assert.Equal(canonical, backoff.ToString());
    }

    [Fact]
    public void Duration_parser_accepts_time_only_iso_fallback()
    {
        Assert.Equal(DurationSyntax.ParseDuration("1m"), DurationSyntax.ParseDuration("PT1M"));
    }

    [Theory]
    [InlineData("P1M")]
    [InlineData("1M")]
    public void Duration_parser_rejects_calendar_units(string expression)
    {
        Assert.Throws<FormatException>(() => DurationSyntax.ParseDuration(expression));
    }

    [Fact]
    public void ParseHuman_Supports_Days()
    {
        Assert.Equal(TimeSpan.FromDays(90), DurationSyntax.ParseHuman("90d"));
        Assert.Equal(TimeSpan.Zero, DurationSyntax.ParseHuman("0d"));
    }

    [Fact]
    public void ParseHuman_Rejects_Unknown_Units_Still()
    {
        Assert.Throws<FormatException>(() => DurationSyntax.ParseHuman("2w"));
    }

    [Fact]
    public void ParseHuman_Rejects_A_Huge_Numeral_As_A_FormatException()
    {
        Assert.Throws<FormatException>(() => DurationSyntax.ParseHuman("99999999999999999999d"));
    }

    [Fact]
    public void TryParse_Rejects_A_Huge_Numeral()
    {
        Assert.False(Backoff.TryParse("99999999999999999999d", out _));
    }

    // Parity with the generator's BackoffExpressionValidator: the multiplier bound must agree so an
    // expression accepted at runtime (e.g. a definition override) is never rejected in a [Job] attribute.
    [Fact]
    public void Multiplier_bound_matches_the_generator()
    {
        Assert.Equal(99999.9999, Backoff.Parse("1s..2s x99999.9999").Multiplier);
        Assert.False(Backoff.TryParse("1s..2s x100000", out _));
    }

    [Fact]
    public void WithJitter_sets_the_fraction()
    {
        var backoff = Backoff.Exponential(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10)).WithJitter(0.3);

        Assert.Equal(0.3, backoff.Jitter);
    }

    [Fact]
    public void ComputeDelaySeconds_rounds_a_sub_second_delay_up_instead_of_collapsing_to_immediate_retry()
    {
        var backoff = Backoff.Fixed(TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, BackoffSchedule.ComputeDelaySeconds(1, backoff));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Exponential_rejects_a_non_finite_multiplier(double multiplier)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), multiplier)
        );
    }

    [Fact]
    public void WithJitter_rejects_a_non_finite_fraction()
    {
        var backoff = Backoff.Default;
        Assert.Throws<ArgumentOutOfRangeException>(() => backoff.WithJitter(double.NaN));
    }

    [Fact]
    public void Fixed_rejects_a_negative_delay()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.Fixed(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Exponential_rejects_max_below_initial()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Backoff.Exponential(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Exponential_rejects_a_multiplier_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Backoff.Exponential(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), multiplier: 0.5)
        );
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void WithJitter_rejects_an_out_of_range_fraction(double fraction)
    {
        var backoff = Backoff.Default;
        Assert.Throws<ArgumentOutOfRangeException>(() => backoff.WithJitter(fraction));
    }

    [Fact]
    public async Task RunStep_projects_the_backoff_into_every_step_scalar()
    {
        var ctx = new StepOptionsCapturingContext();
        var backoff = Backoff.Exponential(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), multiplier: 2.0).WithJitter(0.3);

        await ctx.RunStepAsync("charge", _ => Task.CompletedTask, o => o.WithPolicy(backoff), TestContext.Current.CancellationToken);

        var options = ctx.LastOptions!;
        Assert.Equal(5, options.BackoffInitialDelaySeconds);
        Assert.Equal(600, options.BackoffMaxDelaySeconds);
        Assert.Equal(2.0m, options.BackoffMultiplier);
        Assert.Equal(0.3m, options.BackoffJitter);
    }

    [Fact]
    public async Task A_later_individual_setter_wins_over_Backoff()
    {
        var ctx = new StepOptionsCapturingContext();

        await ctx.RunStepAsync(
            "charge",
            _ => Task.CompletedTask,
            o => o.WithPolicy(Backoff.Exponential(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10))).BackoffMultiplier(3.0),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(3.0m, ctx.LastOptions!.BackoffMultiplier);
    }

    [Fact]
    public async Task RunStep_parses_backoff_expression()
    {
        var ctx = new StepOptionsCapturingContext();

        await ctx.RunStepAsync("charge", _ => Task.CompletedTask, o => o.Backoff("5s..10m x2 ±30%"), TestContext.Current.CancellationToken);

        var options = ctx.LastOptions!;
        Assert.Equal(5, options.BackoffInitialDelaySeconds);
        Assert.Equal(600, options.BackoffMaxDelaySeconds);
        Assert.Equal(2.0m, options.BackoffMultiplier);
        Assert.Equal(0.3m, options.BackoffJitter);
    }
}

/// <summary>
/// Minimal JobContext that captures the StepOptions a void RunStepAsync resolves, so the public
/// configure path can be asserted without reaching the builder's internal surface. Every other sink
/// throws.
/// </summary>
internal sealed class StepOptionsCapturingContext : JobContext
{
    public StepOptions? LastOptions { get; private set; }

    public override long JobId => 1;
    public override string JobNamespace => "test-ns";
    public override short NamespaceId => 1;
    public override string JobName => "step-host";
    public override CancellationToken CancellationToken => CancellationToken.None;

    protected override Task RunStepCoreAsync(string name, Func<CancellationToken, Task> body, StepOptions options, CancellationToken ct)
    {
        LastOptions = options;
        return Task.CompletedTask;
    }

    private static T Unsupported<T>() => throw new NotSupportedException("StepOptionsCapturingContext only captures step options.");

    protected override Task<TResult> RunStepCoreAsync<TResult>(
        string name,
        Func<CancellationToken, Task<TResult>> body,
        StepOptions options,
        CancellationToken ct
    ) => Unsupported<Task<TResult>>();

    protected override Task SetProgressCoreAsync<T>(T value, CancellationToken ct) => Unsupported<Task>();

    protected override Task SetVariableCoreAsync<T>(string name, T value, CancellationToken ct) => Unsupported<Task>();

    protected override Task SetVariableCoreAsync(string name, JobPayload payload, CancellationToken ct) => Unsupported<Task>();

    protected override Task<(bool Found, T? Value)> TryGetVariableCoreAsync<T>(string name, CancellationToken ct)
        where T : default => Unsupported<Task<(bool, T?)>>();

    protected override Task<T> GetOrSetVariableCoreAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> valueFactory,
        CancellationToken ct
    ) => Unsupported<Task<T>>();

    protected override Task<bool> ExistsVariableCoreAsync(string name, CancellationToken ct) => Unsupported<Task<bool>>();

    protected override Task<bool> DeleteVariableCoreAsync(string name, CancellationToken ct) => Unsupported<Task<bool>>();

    protected override Task ResetStateCoreAsync(CancellationToken ct) => Unsupported<Task>();

    protected override Task SleepCoreAsync(string name, TimeSpan? delay, DateTime? resumeAtUtc, string? reason, CancellationToken ct) =>
        Unsupported<Task>();

    protected override Task<SignalWaitOutcome> WaitSignalCoreAsync(string name, CancellationToken ct) =>
        Unsupported<Task<SignalWaitOutcome>>();

    protected override T? DeserializeSignalPayload<T>(byte valueFormatId, byte[] value)
        where T : default => Unsupported<T?>();

    protected override Task<JobEnqueueOutcome> StartChildCoreAsync<TInput>(TInput input, JobEnqueueOptions options, CancellationToken ct) =>
        Unsupported<Task<JobEnqueueOutcome>>();

    protected override Task<JobEnqueueOutcome> StartChildCoreAsync(JobEnqueueRequest request, CancellationToken ct) =>
        Unsupported<Task<JobEnqueueOutcome>>();

    protected override Task<TResult?> GetChildResultCoreAsync<TResult>(long childJobId, CancellationToken ct)
        where TResult : default => Unsupported<Task<TResult?>>();

    protected override Task<Guid?> AcquireLockCoreAsync(string key, LockScope scope, CancellationToken ct) => Unsupported<Task<Guid?>>();

    protected override Task ReleaseLockCoreAsync(string key, LockScope scope, Guid holdToken, CancellationToken ct) => Unsupported<Task>();

    protected override Task WriteNoteCoreAsync<T>(string message, T? detail, CancellationToken ct)
        where T : default => Unsupported<Task>();

    protected override Task RaiseAlertCoreAsync(
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    ) => Unsupported<Task>();
}
