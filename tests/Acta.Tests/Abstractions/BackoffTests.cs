using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Backoff value type: factory invariants, jitter clamping, and the DSL round trip.
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
}
