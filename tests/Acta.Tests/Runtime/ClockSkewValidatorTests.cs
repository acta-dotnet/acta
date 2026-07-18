using Acta.Features.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The worker-init clock-skew guard: skew is estimated against the local-read midpoint, the tightest
/// round-trip wins, and the warn/fail thresholds plus the AllowClockSkew override drive the verdict.
/// </summary>
public sealed class ClockSkewValidatorTests
{
    private static readonly DateTime Base = new(2026, 6, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Warn = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Fail = TimeSpan.FromSeconds(10);

    // --- ClockSkewSample math --------------------------------------------------------------------

    [Fact]
    public void Sample_skew_is_db_minus_local_midpoint()
    {
        var before = Base;
        var after = Base.AddMilliseconds(100);
        var dbNow = Base.AddMilliseconds(50).AddSeconds(1); // midpoint is Base+50ms; DB is 1s ahead of it

        var sample = ClockSkewSample.From(before, dbNow, after);

        Assert.Equal(TimeSpan.FromSeconds(1), sample.Skew);
        Assert.Equal(TimeSpan.FromMilliseconds(100), sample.RoundTrip);
    }

    [Fact]
    public void Sample_skew_is_signed_negative_when_db_trails_local()
    {
        var sample = ClockSkewSample.From(Base, Base.AddSeconds(-3), Base);

        Assert.Equal(TimeSpan.FromSeconds(-3), sample.Skew);
    }

    [Fact]
    public void Best_picks_the_smallest_round_trip()
    {
        var samples = new[]
        {
            new ClockSkewSample(TimeSpan.FromSeconds(9), TimeSpan.FromMilliseconds(100)),
            new ClockSkewSample(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40)),
            new ClockSkewSample(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(80)),
        };

        var best = ClockSkewSample.Best(samples);

        Assert.Equal(TimeSpan.FromMilliseconds(40), best.RoundTrip);
        Assert.Equal(TimeSpan.FromSeconds(1), best.Skew);
    }

    // --- ValidateAsync threshold policy (RTT=0 fixed clock => skew == dbNow - now) ----------------

    // expectWarn=false => WithinTolerance, true => Warned (the internal enum stays out of the public
    // [Theory] signature; CS0051 forbids an internal parameter type on a public test method).
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)] // exactly the warn threshold is still within
    [InlineData(-2, false)]
    [InlineData(5, true)]
    [InlineData(10, true)] // exactly the fail threshold only warns
    [InlineData(-7, true)]
    public async Task Within_and_warn_bands_return_the_verdict_without_throwing(int skewSeconds, bool expectWarn)
    {
        var validator = Validator(skew: TimeSpan.FromSeconds(skewSeconds), allowOverride: false);

        var verdict = await validator.ValidateAsync("billing", CancellationToken.None);

        Assert.Equal(expectWarn ? ClockSkewVerdict.Warned : ClockSkewVerdict.WithinTolerance, verdict);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(-12)]
    public async Task Skew_past_fail_threshold_throws_when_not_allowed(int skewSeconds)
    {
        var validator = Validator(skew: TimeSpan.FromSeconds(skewSeconds), allowOverride: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.ValidateAsync("billing", CancellationToken.None));

        Assert.Contains("clock skew", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AllowClockSkew", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skew_past_fail_threshold_warns_instead_of_throwing_when_allowed()
    {
        var validator = Validator(skew: TimeSpan.FromSeconds(30), allowOverride: true);

        var verdict = await validator.ValidateAsync("billing", CancellationToken.None);

        Assert.Equal(ClockSkewVerdict.ExceededButAllowed, verdict);
    }

    [Fact]
    public async Task Measures_the_configured_number_of_times()
    {
        var calls = 0;
        var clock = new FixedClock(new DateTimeOffset(Base));
        var validator = new ClockSkewValidator(
            _ =>
            {
                calls++;
                return Task.FromResult(Base);
            },
            clock,
            Warn,
            Fail,
            allowOverride: false,
            NullLogger.Instance,
            sampleCount: 3
        );

        await validator.ValidateAsync("billing", CancellationToken.None);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Keeps_the_tightest_sample_across_measurements()
    {
        // Three measurements with growing round-trips; only the first (RTT 0) reports a tolerable skew,
        // the later wide ones would fail. The min-RTT pick must keep the first, so init passes.
        var localTicks = new Queue<DateTime>(
            new[]
            {
                Base,
                Base, // sample 0: RTT 0
                Base,
                Base.AddSeconds(40), // sample 1: RTT 40s
                Base,
                Base.AddSeconds(40), // sample 2: RTT 40s
            }
        );
        var dbReads = new Queue<DateTime>(
            new[]
            {
                Base.AddSeconds(1), // sample 0: skew ~1s
                Base.AddSeconds(100), // sample 1: huge
                Base.AddSeconds(100), // sample 2: huge
            }
        );
        var validator = new ClockSkewValidator(
            _ => Task.FromResult(dbReads.Dequeue()),
            new QueueClock(localTicks),
            Warn,
            Fail,
            allowOverride: false,
            NullLogger.Instance,
            sampleCount: 3
        );

        var verdict = await validator.ValidateAsync("billing", CancellationToken.None);

        Assert.Equal(ClockSkewVerdict.WithinTolerance, verdict);
    }

    private static ClockSkewValidator Validator(TimeSpan skew, bool allowOverride) =>
        new(_ => Task.FromResult(Base + skew), new FixedClock(new DateTimeOffset(Base)), Warn, Fail, allowOverride, NullLogger.Instance);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class QueueClock(Queue<DateTime> ticks) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(ticks.Dequeue(), DateTimeKind.Utc));
    }
}
