using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// StepOptionsBuilder.MaxAttempts stays inside the persisted 16-bit range: the runtime narrows the
/// override to a short, so an unbounded value would wrap negative and the provider routines'
/// attempt_number comparison would exhaust the step on its first failure.
/// </summary>
public sealed class StepOptionsBuilderTests
{
    [Fact]
    public void MaxAttempts_rejects_values_past_the_persisted_range()
    {
        var builder = new StepOptionsBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.MaxAttempts(short.MaxValue + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.MaxAttempts(0));
    }

    [Fact]
    public void MaxAttempts_accepts_the_full_persisted_range()
    {
        var options = new StepOptionsBuilder().MaxAttempts(short.MaxValue).Build();

        Assert.Equal(short.MaxValue, options.MaxAttempts);
    }
}
