using Acta.Features.Workers;
using Acta.Redis.Configuration;
using Acta.Redis.Features.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The Redis wake transport contract: a down Redis costs latency only (local-first wake still
/// reaches the publishing process's own waiters; nothing throws), and with a live server -
/// <c>ACTA_TEST_REDIS</c> gates these - a wake published by one transport instance reaches waiters
/// on another, for both worker-namespace and job-completion channels. Each test isolates itself
/// with a unique channel prefix so parallel runs sharing a Redis never cross-wake. The
/// <c>UseRedisWakeup</c> facts cover the builder wiring itself: the in-process registration is
/// replaced, a host-supplied multiplexer wins, and a missing configuration fails loudly.
/// </summary>
public sealed class RedisWakeupTests
{
    private static readonly TimeSpan WaitGenerously = TimeSpan.FromSeconds(10);
    private static readonly CancellationToken None = CancellationToken.None;

    // Constructs without a reachable server (the factory forces AbortOnConnectFail=false) and fails
    // fast in tests instead of waiting out the default connect timeout.
    private const string UnreachableConfiguration = "127.0.0.1:1,connectTimeout=200,connectRetry=0";

    private static RedisWakeupOptions UniquePrefix() => new() { ChannelPrefix = "acta-test-" + Guid.NewGuid().ToString("N") };

    // ---- UseRedisWakeup builder wiring ----

    [Fact]
    public void UseRedisWakeup_replaces_the_in_process_registration()
    {
        // Mirror UseActa's ordering: the in-process default is TryAdd'ed first, the builder
        // callback (where UseRedisWakeup runs) executes afterwards - Replace must win regardless.
        var services = new ServiceCollection();
        services.TryAddSingleton<IWorkerWakeup, InProcessWakeup>();

        new JobsBuilder(services).UseRedisWakeup(UnreachableConfiguration);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<RedisWakeup>(provider.GetRequiredService<IWorkerWakeup>());
        Assert.NotNull(provider.GetRequiredService<IConnectionMultiplexer>());
    }

    [Fact]
    public void UseRedisWakeup_prefers_a_host_registered_multiplexer()
    {
        var config = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectTimeout = 200,
            ConnectRetry = 0,
        };
        config.EndPoints.Add("127.0.0.1", 1);
        using var hostOwned = ConnectionMultiplexer.Connect(config);

        var services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(hostOwned);

        // No Configuration on the options: with a host-registered multiplexer none is needed.
        new JobsBuilder(services).UseRedisWakeup(o => o.ChannelPrefix = "acta-test");

        using var provider = services.BuildServiceProvider();
        Assert.Same(hostOwned, provider.GetRequiredService<IConnectionMultiplexer>());
        Assert.IsType<RedisWakeup>(provider.GetRequiredService<IWorkerWakeup>());
    }

    [Fact]
    public void UseRedisWakeup_without_configuration_or_multiplexer_throws_on_resolve()
    {
        var services = new ServiceCollection();
        new JobsBuilder(services).UseRedisWakeup(o => o.ChannelPrefix = "acta-test");

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IWorkerWakeup>());
        Assert.Contains("RedisWakeupOptions.Configuration", ex.Message);
    }

    [Fact]
    public async Task Two_di_wired_hosts_relay_wakes_through_redis()
    {
        var configuration = Environment.GetEnvironmentVariable("ACTA_TEST_REDIS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(configuration), "ACTA_TEST_REDIS not set: no Redis server to test against.");

        var prefix = "acta-test-" + Guid.NewGuid().ToString("N");
        using var receiverHost = BuildHost(configuration!, prefix);
        using var senderHost = BuildHost(configuration!, prefix);

        var receiver = receiverHost.GetRequiredService<IWorkerWakeup>();
        var wait = receiver.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask();
        await Task.Delay(250, None); // let the receiver's pattern subscription land - pub/sub has no replay

        // Publish through the same guard production call sites use, against the DI-resolved wakeup.
        var publisher = new WorkerWakeupPublisher(senderHost.GetRequiredService<IWorkerWakeup>());
        await publisher.WakeAsync(
            WorkerWakeupChannel.WorkerNamespace("billing"),
            WorkerWakeupReason.WorkAvailable,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await wait.WaitAsync(WaitGenerously, None));

        static ServiceProvider BuildHost(string configuration, string prefix)
        {
            var services = new ServiceCollection();
            services.TryAddSingleton<IWorkerWakeup, InProcessWakeup>();
            new JobsBuilder(services).UseRedisWakeup(o =>
            {
                o.Configuration = configuration;
                o.ChannelPrefix = prefix;
            });
            return services.BuildServiceProvider();
        }
    }

    [Fact]
    public async Task Redis_down_degrades_to_local_wakes_and_never_throws()
    {
        // An unreachable endpoint with AbortOnConnectFail=false: the multiplexer constructs fine and
        // keeps retrying in the background - exactly the outage posture UseRedisWakeup configures.
        var config = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectTimeout = 250,
            ConnectRetry = 1,
        };
        config.EndPoints.Add("127.0.0.1", 1);
        using var redis = await ConnectionMultiplexer.ConnectAsync(config);

        await using var wakeup = new RedisWakeup(redis, Options.Create(UniquePrefix()));

        // The wait's subscription attempt fails (logged, swallowed); the local wait still runs.
        var wait = wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask();

        // The wake must not throw despite Redis being down, and must reach the LOCAL waiter -
        // local-first delivery is what makes a Redis outage a latency event, not a correctness one.
        await wakeup.WakeAsync(
            WorkerWakeupChannel.WorkerNamespace("billing"),
            WorkerWakeupReason.WorkAvailable,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await wait.WaitAsync(WaitGenerously, None));
    }

    [Fact]
    public async Task A_wake_published_by_one_instance_reaches_waiters_on_another()
    {
        var configuration = Environment.GetEnvironmentVariable("ACTA_TEST_REDIS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(configuration), "ACTA_TEST_REDIS not set: no Redis server to test against.");

        using var redis = await ConnectionMultiplexer.ConnectAsync(configuration!);
        var prefix = UniquePrefix();
        await using var receiver = new RedisWakeup(redis, Options.Create(prefix));
        await using var sender = new RedisWakeup(redis, Options.Create(prefix));

        // Worker-namespace relay (jittered on the remote side; the timeout is generous).
        var nsWait = receiver.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask();
        await Task.Delay(250, None); // let the receiver's pattern subscription land - pub/sub has no replay
        await sender.WakeAsync(
            WorkerWakeupChannel.WorkerNamespace("billing"),
            WorkerWakeupReason.WorkAvailable,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(WorkerWakeupWaitResult.Signaled, await nsWait.WaitAsync(WaitGenerously, None));

        // Job-completion relay (never jittered; reaches existing waiters only on the remote side too).
        var jobWait = receiver.WaitAsync(WorkerWakeupChannel.JobCompletion(12345), WaitGenerously, None).AsTask();
        await Task.Delay(100, None);
        await sender.WakeAsync(
            WorkerWakeupChannel.JobCompletion(12345),
            WorkerWakeupReason.JobFinished,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(WorkerWakeupWaitResult.Signaled, await jobWait.WaitAsync(WaitGenerously, None));
    }
}
