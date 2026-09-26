using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// StepOptionsBuilder.MaxAttempts refuses a budget below one attempt.
/// </summary>
public sealed class StepOptionsBuilderTests
{
    [Fact]
    public void MaxAttempts_rejects_values_below_one()
    {
        var builder = new StepOptionsBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.MaxAttempts(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.MaxAttempts(-1));
    }
}
