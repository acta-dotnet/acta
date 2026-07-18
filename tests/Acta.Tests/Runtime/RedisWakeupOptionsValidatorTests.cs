using Acta.Redis.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Proves a negative <see cref="RedisWakeupOptions.RemoteWakeJitterMax"/> is rejected before a host
/// can boot (it would flip the jitter delay's random range).
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
}
