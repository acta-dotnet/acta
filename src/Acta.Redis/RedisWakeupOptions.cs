using StackExchange.Redis;

namespace Acta.Redis;

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
    /// published by this process reach its own waiters directly with no delay. At most one delayed
    /// wake is ever pending per channel, so a burst of duplicate messages costs one timer, not one
    /// per message. Default 50ms; set <see cref="TimeSpan.Zero"/> to relay every wake immediately.
    /// Capped at <see cref="MaxRemoteWakeJitter"/>.
    /// </summary>
    public TimeSpan RemoteWakeJitter { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Upper bound on <see cref="RemoteWakeJitter"/>, matching <c>JobsOptions.ClaimIdleJitterMax</c>.
    /// Jitter exists to spread a claim herd across a moment, not to defer work: past a second it is
    /// competing with the poll floor that would have found the job anyway.
    /// </summary>
    public static readonly TimeSpan MaxRemoteWakeJitter = TimeSpan.FromSeconds(1);
}
