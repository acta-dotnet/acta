using Acta.Redis.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Proves <see cref="RedisWakeupOptions.RemoteWakeJitterMax"/> is bounded at both ends before a host
/// can boot: a negative flips the jitter delay's random range, and an unbounded one overflows the
/// tick arithmetic that picks the delay.
/// </summary>
public sealed class RedisWakeupOptionsValidatorTests
{
    private static readonly RedisWakeupOptionsValidator Validator = new();

    [Fact]
    public void Default_options_pass()
    {
        var result = Validator.Validate(name: null, new RedisWakeupOptions());
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Negative_RemoteWakeJitterMax_fails()
    {
        var result = Validator.Validate(name: null, new RedisWakeupOptions { RemoteWakeJitterMax = TimeSpan.FromMilliseconds(-1) });
        Assert.True(result.Failed);
        Assert.Contains("RemoteWakeJitterMax", result.FailureMessage);
    }

    [Fact]
    public void Zero_RemoteWakeJitterMax_passes()
    {
        var result = Validator.Validate(name: null, new RedisWakeupOptions { RemoteWakeJitterMax = TimeSpan.Zero });
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void The_cap_itself_passes()
    {
        var result = Validator.Validate(
            name: null,
            new RedisWakeupOptions { RemoteWakeJitterMax = RedisWakeupOptions.MaxRemoteWakeJitter }
        );
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public void Above_the_cap_fails()
    {
        var result = Validator.Validate(
            name: null,
            new RedisWakeupOptions { RemoteWakeJitterMax = RedisWakeupOptions.MaxRemoteWakeJitter + TimeSpan.FromMilliseconds(1) }
        );
        Assert.True(result.Failed);
        Assert.Contains("RemoteWakeJitterMax", result.FailureMessage);
    }

    // TimeSpan.MaxValue used to validate, then overflow `RemoteWakeJitterMax.Ticks + 1` to long.MinValue
    // on the Redis subscriber thread, where nothing catches. The cap is what stops it reaching that.
    [Fact]
    public void MaxValue_RemoteWakeJitterMax_fails()
    {
        var result = Validator.Validate(name: null, new RedisWakeupOptions { RemoteWakeJitterMax = TimeSpan.MaxValue });
        Assert.True(result.Failed);
        Assert.Contains("RemoteWakeJitterMax", result.FailureMessage);
    }
}
