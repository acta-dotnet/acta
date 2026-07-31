using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Defaults that are part of the public contract; a change here is a behavior change for every
/// deployment that does not set the option.
/// </summary>
public sealed class JobsOptionsDefaultsTests
{
    [Fact]
    public void Default_ClaimBatchSize_is_32()
    {
        Assert.Equal(32, new JobsOptions().ClaimBatchSize);
    }

    [Fact]
    public void Default_ExclusiveKeyBounceDelaySeconds_is_2()
    {
        Assert.Equal(2, new JobsOptions().ExclusiveKeyBounceDelaySeconds);
    }
}
