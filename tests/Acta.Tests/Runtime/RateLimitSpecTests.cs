using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The rate-limit format, which the generator diagnostic, registration, the override gate, and
/// admission all read through one parser: what counts as a rate, how a rate becomes a whole-
/// millisecond interval, and where the ceiling is.
/// </summary>
public sealed class RateLimitSpecTests
{
    [Theory]
    [InlineData("10/s", 10, 100)]
    [InlineData("1/s", 1, 1000)]
    [InlineData("1000/s", 1000, 1)]
    [InlineData("600/m", 600, 100)]
    [InlineData("1/m", 1, 60_000)]
    [InlineData("5000/h", 5000, 720)]
    [InlineData("1/h", 1, 3_600_000)]
    public void A_rate_resolves_to_a_burst_and_a_whole_millisecond_interval(string text, int count, int intervalMilliseconds)
    {
        Assert.True(RateLimitSpec.TryParse(text, out var spec, out _));

        Assert.Equal(count, spec.Count);
        Assert.Equal(intervalMilliseconds, spec.IntervalMilliseconds);
        Assert.Equal(text, spec.Text);
    }

    [Fact]
    public void An_interval_that_does_not_divide_evenly_rounds_up()
    {
        // 1000/3 is 333.33ms; rounding down would realize 3.003/s, which is over the declared rate.
        Assert.True(RateLimitSpec.TryParse("3/s", out var spec, out _));

        Assert.Equal(334, spec.IntervalMilliseconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("10")]
    [InlineData("/s")]
    [InlineData("10/")]
    [InlineData("10/d")]
    [InlineData("10/sec")]
    [InlineData("0/s")]
    [InlineData("-1/s")]
    [InlineData("1.5/s")]
    [InlineData("+1/s")]
    [InlineData("10 /s")]
    [InlineData("1001/s")]
    [InlineData("60001/m")]
    [InlineData("3600001/h")]
    [InlineData("99999999999999999/h")]
    public void Anything_that_is_not_a_rate_is_rejected_with_a_reason(string? text)
    {
        Assert.False(RateLimitSpec.TryParse(text, out _, out var error));

        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void The_ceiling_of_each_period_is_one_admission_per_millisecond()
    {
        // The store keeps the arrival time to the millisecond, so this is the fastest meter that moves.
        Assert.True(RateLimitSpec.TryParse("60000/m", out var perMinute, out _));
        Assert.True(RateLimitSpec.TryParse("3600000/h", out var perHour, out _));

        Assert.Equal(1, perMinute.IntervalMilliseconds);
        Assert.Equal(1, perHour.IntervalMilliseconds);
    }

    [Fact]
    public void Parse_throws_the_reason_TryParse_reports()
    {
        var ex = Assert.Throws<ArgumentException>(() => RateLimitSpec.Parse("10/d"));

        Assert.Contains("period is s, m, or h", ex.Message, StringComparison.Ordinal);
    }
}
