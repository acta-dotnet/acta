using Acta.Features.Workers;
using Acta.Redis.Configuration;
using Acta.Redis.Features.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Acta;

/// <summary>
/// Wires the Redis wake transport into an Acta deployment:
/// <c>j.UseRedisWakeup("localhost:6379")</c>. Replaces the in-process <see cref="IWorkerWakeup"/>
/// registration so enqueues, control verbs, and completions wake workers and completion waiters in
/// OTHER processes too; everything else (the publish guard, metrics, poll floors, claim semantics)
/// is unchanged: Redis is a latency accelerator, never a correctness dependency.
/// </summary>
public static class RedisActaBuilderExtensions
{
    /// <summary>
    /// Use Redis pub/sub as the wake transport. Registers a <see cref="IConnectionMultiplexer"/>
    /// from <see cref="RedisWakeupOptions.Configuration"/> unless the host already registered one.
    /// </summary>
    public static IActaBuilder UseRedisWakeup(this IActaBuilder builder, Action<RedisWakeupOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(configure);
        builder.Services.AddOptions<RedisWakeupOptions>().ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RedisWakeupOptions>, RedisWakeupOptionsValidator>());
        builder.Services.TryAddSingleton<IConnectionMultiplexer>(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisWakeupOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.Configuration))
            {
                throw new InvalidOperationException(
                    "RedisWakeupOptions.Configuration is required when the host registers no IConnectionMultiplexer."
                );
            }

            var config = ConfigurationOptions.Parse(options.Configuration);
            // The wake transport is best-effort: come up without Redis and let the multiplexer
            // connect/reconnect in the background instead of failing host startup.
            config.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(config);
        });
        builder.Services.Replace(ServiceDescriptor.Singleton<IWorkerWakeup, RedisWakeup>());
        return builder;
    }

    /// <summary>
    /// Use Redis pub/sub as the wake transport, connecting with the given StackExchange.Redis
    /// configuration string (e.g. <c>"localhost:6379"</c>).
    /// </summary>
    public static IActaBuilder UseRedisWakeup(this IActaBuilder builder, string configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        return builder.UseRedisWakeup(o => o.Configuration = configuration);
    }
}
