using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Acta.Redis.Configuration;

/// <summary>
/// Settings for the Redis wake transport.
/// </summary>
public sealed class RedisWakeupOptions
{
    /// <summary>
    /// StackExchange.Redis configuration string (e.g. <c>"localhost:6379"</c>). Required unless the
    /// host registers its own <see cref="IConnectionMultiplexer"/>, which then takes precedence.
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Prefix of every Redis channel this transport publishes and subscribes
    /// (<c>{prefix}:wake:{channel}</c>). Give each environment sharing a Redis instance its own
    /// prefix so wakes never bleed across environments.
    /// </summary>
    public string ChannelPrefix { get; set; } = "acta";

    /// <summary>
    /// Maximum random delay applied to a REMOTE worker-namespace wake before it reaches local
    /// waiters, so N processes woken by one publish claim staggered instead of stampeding the claim
    /// index. Job-completion wakes are never jittered (single waiter, latency-priority), and wakes
    /// published by this process reach its own waiters directly with no delay. Default 50ms.
    /// </summary>
    public TimeSpan RemoteWakeJitterMax { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Validates <see cref="RedisWakeupOptions"/> at host startup (paired with <c>ValidateOnStart</c>).
/// </summary>
internal sealed class RedisWakeupOptionsValidator : IValidateOptions<RedisWakeupOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisWakeupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.RemoteWakeJitterMax < TimeSpan.Zero
            ? ValidateOptionsResult.Fail("RedisWakeupOptions.RemoteWakeJitterMax must be >= 0.")
            : ValidateOptionsResult.Success;
    }
}
