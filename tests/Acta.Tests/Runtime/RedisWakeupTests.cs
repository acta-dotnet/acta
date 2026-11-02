using System.Diagnostics;
using Acta.Redis;
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

    /// <summary>
    /// A hang guard, not a measurement: only a wake that never arrives, or a trial that never runs clean, reaches it.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    // Constructs without a reachable server (the factory forces AbortOnConnectFail=false) and fails
    // fast in tests instead of waiting out the default connect timeout.
    private const string UnreachableConfiguration = "127.0.0.1:1,connectTimeout=200,connectRetry=0";

    private static RedisWakeupOptions UniquePrefix() => new() { ChannelPrefix = "acta-test-" + Guid.NewGuid().ToString("N") };

    /// <summary>
    /// Publishes until a receiver's wait is signaled, then returns how the last wait ended. Pub/sub has no replay, so a
    /// wake sent before the receiver's pattern subscription lands is gone; republishing makes the delivery fact
    /// independent of how long the subscribe takes. A wait subscribes once, as it starts, and a failed subscribe is
    /// swallowed, so a wait that times out is replaced by a fresh one, which subscribes again. The hang guard ends the
    /// loop, so a wake that never arrives still fails as TimedOut rather than spinning.
    /// </summary>
    private static async Task<WorkerWakeupWaitStatus> PublishUntilSignaledAsync(
        Func<Task<WorkerWakeupWaitStatus>> startWait,
        Func<Task> publish
    )
    {
        var publishing = Stopwatch.StartNew();
        while (true)
        {
            var wait = startWait();
            while (!wait.IsCompleted)
            {
                await publish();
                await Task.WhenAny(wait, Task.Delay(50, None));
            }

            var status = await wait;
            if (status != WorkerWakeupWaitStatus.TimedOut || publishing.Elapsed >= HangGuard)
            {
                return status;
            }
        }
    }

    [Fact]
    public void UseRedisWakeup_replaces_the_in_process_registration()
    {
        // Mirror UseActa's ordering: the in-process default is TryAdd'ed first, the builder
        // callback (where UseRedisWakeup runs) executes afterwards - Replace must win regardless.
        var services = new ServiceCollection();
        services.TryAddSingleton<IWorkerWakeup, InProcessWakeup>();

        new ActaBuilder(services).UseRedisWakeup(UnreachableConfiguration);

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
        new ActaBuilder(services).UseRedisWakeup(o => o.ChannelPrefix = "acta-test");

        using var provider = services.BuildServiceProvider();
        Assert.Same(hostOwned, provider.GetRequiredService<IConnectionMultiplexer>());
        Assert.IsType<RedisWakeup>(provider.GetRequiredService<IWorkerWakeup>());
    }

    [Fact]
    public void UseRedisWakeup_without_configuration_or_multiplexer_throws_on_resolve()
    {
        var services = new ServiceCollection();
        new ActaBuilder(services).UseRedisWakeup(o => o.ChannelPrefix = "acta-test");

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

        // Publish through the same guard production call sites use, against the DI-resolved wakeup.
        var publisher = new WorkerWakeupPublisher(senderHost.GetRequiredService<IWorkerWakeup>());
        Assert.Equal(
            WorkerWakeupWaitStatus.Signaled,
            await PublishUntilSignaledAsync(
                () => receiver.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask(),
                () =>
                    publisher
                        .WakeAsync(
                            WorkerWakeupChannel.WorkerNamespace("billing"),
                            WorkerWakeupReason.WorkAvailable,
                            TestContext.Current.CancellationToken
                        )
                        .AsTask()
            )
        );

        static ServiceProvider BuildHost(string configuration, string prefix)
        {
            var services = new ServiceCollection();
            services.TryAddSingleton<IWorkerWakeup, InProcessWakeup>();
            new ActaBuilder(services).UseRedisWakeup(o =>
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
        // It fails fast, so the wait's subscribe fails at once: the fact is local delivery, not how
        // long a queued subscribe takes.
        var config = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ConnectTimeout = 250,
            ConnectRetry = 1,
            BacklogPolicy = BacklogPolicy.FailFast,
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

        Assert.Equal(WorkerWakeupWaitStatus.Signaled, await wait.WaitAsync(HangGuard, None));
    }

    [Fact]
    public async Task A_remote_wake_after_a_zero_jitter_draw_is_still_delivered()
    {
        // Fail fast, so each wait's subscription attempt fails at once instead of queuing until a timeout.
        var config = ConfigurationOptions.Parse(UnreachableConfiguration);
        config.AbortOnConnectFail = false;
        config.BacklogPolicy = BacklogPolicy.FailFast;
        using var redis = await ConnectionMultiplexer.ConnectAsync(config);
        var options = UniquePrefix();
        await using var wakeup = new RedisWakeup(redis, Options.Create(options)) { JitterSource = new ZeroDraw() };
        var channel = WorkerWakeupChannel.WorkerNamespace("billing");
        var message = RedisChannel.Literal(options.ChannelPrefix + ":wake:" + channel.Name);

        wakeup.OnRemoteWake(message, RedisValue.EmptyString);
        Assert.Equal(WorkerWakeupWaitStatus.Signaled, await wakeup.WaitAsync(channel, WaitGenerously, None));

        wakeup.OnRemoteWake(message, RedisValue.EmptyString);
        Assert.Equal(WorkerWakeupWaitStatus.Signaled, await wakeup.WaitAsync(channel, WaitGenerously, None));
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
        Assert.Equal(
            WorkerWakeupWaitStatus.Signaled,
            await PublishUntilSignaledAsync(
                () => receiver.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask(),
                () =>
                    sender
                        .WakeAsync(
                            WorkerWakeupChannel.WorkerNamespace("billing"),
                            WorkerWakeupReason.WorkAvailable,
                            TestContext.Current.CancellationToken
                        )
                        .AsTask()
            )
        );

        // Job-completion relay (never jittered; reaches existing waiters only on the remote side too).
        Assert.Equal(
            WorkerWakeupWaitStatus.Signaled,
            await PublishUntilSignaledAsync(
                () => receiver.WaitAsync(WorkerWakeupChannel.JobCompletion(12345), WaitGenerously, None).AsTask(),
                () =>
                    sender
                        .WakeAsync(
                            WorkerWakeupChannel.JobCompletion(12345),
                            WorkerWakeupReason.JobFinished,
                            TestContext.Current.CancellationToken
                        )
                        .AsTask()
            )
        );
    }

    [Fact]
    public async Task A_duplicate_wake_burst_holds_one_pending_wake_per_channel()
    {
        var configuration = Environment.GetEnvironmentVariable("ACTA_TEST_REDIS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(configuration), "ACTA_TEST_REDIS not set: no Redis server to test against.");

        using var redis = await ConnectionMultiplexer.ConnectAsync(configuration!);
        var prefix = UniquePrefix();

        // The full jitter window, drawn at its longest, so every wake this burst schedules is still pending
        // when it ends and the count below is the number of timers the burst actually allocated.
        prefix.RemoteWakeJitter = RedisWakeupOptions.MaxRemoteWakeJitter;
        await using var receiver = new RedisWakeup(redis, Options.Create(prefix)) { JitterSource = new MaxDraw() };

        // No publish window, so every wake of the burst reaches the receiver as its own message.
        await using var sender = new RedisWakeup(redis, Options.Create(prefix)) { PublishWindow = TimeSpan.Zero };

        // Prove the receiver's namespace subscription is live before the burst, on a channel of its
        // own: republishing on "billing" until it lands would inflate the very count under test. One
        // pattern subscription covers every namespace channel, so a live "warmup" proves "billing"
        // live too, and the count it adds is taken off as the baseline.
        Assert.Equal(
            WorkerWakeupWaitStatus.Signaled,
            await PublishUntilSignaledAsync(
                () => receiver.WaitAsync(WorkerWakeupChannel.WorkerNamespace("warmup"), WaitGenerously, None).AsTask(),
                () => sender.WakeAsync(WorkerWakeupChannel.WorkerNamespace("warmup"), WorkerWakeupReason.WorkAvailable, None).AsTask()
            )
        );
        await Task.Delay(RedisWakeupOptions.MaxRemoteWakeJitter + TimeSpan.FromMilliseconds(100), None);
        var baseline = receiver.JitteredWakesScheduled;

        var wait = receiver.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask();
        var sentinel = receiver.WaitAsync(WorkerWakeupChannel.WorkerNamespace("sentinel"), HangGuard, None).AsTask();

        // Two channels, many duplicates each: useful state is one pending wake per channel, not per message.
        for (var i = 0; i < 200; i++)
        {
            await sender.WakeAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WorkerWakeupReason.WorkAvailable, None);
            await sender.WakeAsync(WorkerWakeupChannel.WorkerNamespace("shipping"), WorkerWakeupReason.WorkAvailable, None);
        }

        // Pub/sub keeps one publisher's order, so a sentinel published behind the burst lands after every message of it:
        // once its wake arrives the receiver has handled the whole burst, however slowly it was delivered.
        await sender.WakeAsync(WorkerWakeupChannel.WorkerNamespace("sentinel"), WorkerWakeupReason.WorkAvailable, None);
        Assert.Equal(WorkerWakeupWaitStatus.Signaled, await sentinel);

        // The point of the fix: work tracks distinct channels, not messages. 400 messages over 2
        // channels inside one jitter window schedule a couple of delayed wakes; uncoalesced this was
        // 400. The slack covers a window elapsing mid-burst and letting a channel schedule again. The
        // sentinel's own wake is taken off.
        var scheduled = receiver.JitteredWakesScheduled - baseline - 1;
        Assert.True(scheduled is >= 1 and <= 20, $"expected a per-channel count, got {scheduled} scheduled wakes for 400 messages.");

        // And the wake still lands: coalescing drops duplicates, never the delivery they stand for.
        Assert.Equal(WorkerWakeupWaitStatus.Signaled, await wait.WaitAsync(WaitGenerously, None));
    }

    [Fact]
    public async Task A_burst_of_namespace_wakes_publishes_once_and_once_more_when_its_window_ends()
    {
        // Fail fast, so each publish's failure costs nothing; the count is taken before the publish.
        var config = ConfigurationOptions.Parse(UnreachableConfiguration);
        config.AbortOnConnectFail = false;
        config.BacklogPolicy = BacklogPolicy.FailFast;
        using var redis = await ConnectionMultiplexer.ConnectAsync(config);
        var window = TimeSpan.FromMilliseconds(500);
        var billing = WorkerWakeupChannel.WorkerNamespace("billing");

        // A trial counts only when its burst ends inside half the window, timed from before the first billing wake,
        // so the count is read before the trailing publish can run. A slower burst may outlive the window, so it
        // proves nothing and the trial runs again on a fresh instance.
        var trials = Stopwatch.StartNew();
        while (true)
        {
            Assert.True(trials.Elapsed < HangGuard, "no burst ended inside half its publish window.");
            await using var wakeup = new RedisWakeup(redis, Options.Create(UniquePrefix())) { PublishWindow = window };

            // A completion wake is never held back; it also takes the first publish's cost out of the burst.
            await wakeup.WakeAsync(WorkerWakeupChannel.JobCompletion(7), WorkerWakeupReason.JobFinished, None);
            Assert.Equal(1, wakeup.Published);

            var burst = Stopwatch.StartNew();
            for (var i = 0; i < 100; i++)
            {
                await wakeup.WakeAsync(billing, WorkerWakeupReason.WorkAvailable, None);
            }
            var published = wakeup.Published;
            if (burst.Elapsed >= window / 2)
            {
                continue;
            }

            // The first wake published, and the other 99 share one trailing publish at the window's end.
            Assert.Equal(2, published);
            var deadline = DateTime.UtcNow + HangGuard;
            while (wakeup.Published < 3)
            {
                Assert.True(DateTime.UtcNow < deadline, "the trailing publish never ran.");
                await Task.Delay(10, None);
            }

            // Another namespace keeps a window of its own.
            await wakeup.WakeAsync(WorkerWakeupChannel.WorkerNamespace("shipping"), WorkerWakeupReason.WorkAvailable, None);
            Assert.Equal(4, wakeup.Published);
            return;
        }
    }

    [Fact]
    public async Task Only_a_wait_on_a_job_subscribes_to_job_completions()
    {
        var configuration = Environment.GetEnvironmentVariable("ACTA_TEST_REDIS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(configuration), "ACTA_TEST_REDIS not set: no Redis server to test against.");

        using var redis = await ConnectionMultiplexer.ConnectAsync(configuration!);
        await using var wakeup = new RedisWakeup(redis, Options.Create(UniquePrefix()));

        // A claim loop's wait hears namespace wakes only, so a worker process never receives job completions.
        await wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), TimeSpan.FromMilliseconds(1), None);
        Assert.False(wakeup.HearsCompletions);

        // A failed subscribe is swallowed and the next wait tries again, so the fact is that a job wait subscribes.
        var subscribing = Stopwatch.StartNew();
        do
        {
            Assert.True(subscribing.Elapsed < HangGuard, "no wait on a job subscribed to job completions.");
            await wakeup.WaitAsync(WorkerWakeupChannel.JobCompletion(1), TimeSpan.FromMilliseconds(1), None);
        } while (!wakeup.HearsCompletions);
    }

    private sealed class ZeroDraw : Random
    {
        public override long NextInt64(long maxValue) => 0;
    }

    private sealed class MaxDraw : Random
    {
        public override long NextInt64(long maxValue) => maxValue - 1;
    }
}
