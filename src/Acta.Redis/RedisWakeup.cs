using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Acta.Redis;

/// <summary>
/// Redis-backed <see cref="IWorkerWakeup"/>: composes the in-process transport for local delivery and
/// relays wakes across processes via Redis pub/sub. A wake publishes locally FIRST (the publishing
/// process's waiters never wait on a network round trip), then fire-and-forgets one Redis message,
/// a job-completion wake on <c>{prefix}:done:{channel.Name}</c> and any other on
/// <c>{prefix}:wake:{channel.Name}</c>; every process subscribed to that route delivers it into its
/// own local transport. Redis being down, slow, or lossy costs latency only: publishes are
/// fire-and-forget, the multiplexer reconnects on its own, and every waiter keeps its poll floor.
/// </summary>
/// <remarks>
/// Redis delivers a message to every process subscribed to its route, whether or not anything there
/// waits, so the transport sends each process only what its waiters can use: a process subscribes to
/// a route when it first waits on it, so a worker process never hears another caller's job
/// completions; a namespace's wakes are published at most once per <see cref="PublishWindow"/>; and
/// a remote worker-namespace wake is jittered per <see cref="RedisWakeupOptions.RemoteWakeJitter"/>.
/// A remote job-completion wake reaches existing waiters only (the local transport never allocates
/// for it). A subscription also receives this process's own publishes; that loopback re-wake
/// coalesces onto the local latch and is harmless.
/// </remarks>
internal sealed class RedisWakeup : IWorkerWakeup, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// How long one published namespace wake stands for the later ones: a wake inside the window
    /// schedules a single trailing publish at its end, which every other wake in the window shares,
    /// so a stream of enqueues publishes each namespace's wake about a hundred times a second
    /// instead of once per job, and a remote claim loop hears of new work at most one window late.
    /// Every suppressed wake's work commits before the trailing publish, so none is lost.
    /// </summary>
    internal static readonly TimeSpan DefaultPublishWindow = TimeSpan.FromMilliseconds(10);

    private readonly IConnectionMultiplexer _redis;
    private readonly InProcessWakeup _local = new();
    private readonly string _wakePrefix;
    private readonly string _donePrefix;
    private readonly Subscription _wakes;
    private readonly Subscription _completions;
    private readonly TimeSpan _remoteJitterMax;

    /// <summary>
    /// Channels with a delayed wake already scheduled. Set membership is the whole state: the value is
    /// unused, and ConcurrentDictionary is just the set primitive .NET does not otherwise ship.
    /// </summary>
    private readonly ConcurrentDictionary<WorkerWakeupChannel, byte> _pendingJittered = new();

    /// <summary>One publish window per namespace channel this process has woken; the keyspace is the namespaces.</summary>
    private readonly ConcurrentDictionary<string, PublishThrottle> _throttles = new(StringComparer.Ordinal);
    private readonly ILogger _log;

    private long _jitteredWakesScheduled;
    private long _published;

    /// <summary>
    /// How many delayed wakes this instance has actually scheduled, which is the cost coalescing
    /// exists to bound: it tracks distinct channels, not received messages. Test seam, since the
    /// coalescing has no other outward sign.
    /// </summary>
    internal long JitteredWakesScheduled => Interlocked.Read(ref _jitteredWakesScheduled);

    /// <summary>How many messages this instance has published to Redis. Test seam, since the publish window has no other outward sign.</summary>
    internal long Published => Interlocked.Read(ref _published);

    /// <summary>Whether this process has subscribed to job completions. Test seam for the per-route subscription.</summary>
    internal bool HearsCompletions => _completions.Active;

    /// <summary>Draws each remote wake's jitter. Test seam, since a zero draw is otherwise left to chance.</summary>
    internal Random JitterSource { get; init; } = Random.Shared;

    /// <summary>The namespace publish window, <see cref="DefaultPublishWindow"/>. Test seam, so a burst fits inside one window on a slow machine.</summary>
    internal TimeSpan PublishWindow { get; init; } = DefaultPublishWindow;

    public RedisWakeup(IConnectionMultiplexer redis, IOptions<RedisWakeupOptions> options, ILogger<RedisWakeup>? log = null)
    {
        _redis = redis;
        _wakePrefix = options.Value.ChannelPrefix + ":wake:";
        _donePrefix = options.Value.ChannelPrefix + ":done:";
        _wakes = new(RedisChannel.Pattern(_wakePrefix + "*"));
        _completions = new(RedisChannel.Pattern(_donePrefix + "*"));
        _remoteJitterMax = options.Value.RemoteWakeJitter;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public async ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
    {
        // Local first: the publishing process's own waiters wake without a network round trip even
        // when Redis is unreachable.
        await _local.WakeAsync(channel, reason, ct);

        // An admission wake is this host's own: every host runs its own schedule admitter.
        if (channel.Name.StartsWith(WorkerWakeupChannel.AdmissionPrefix, StringComparison.Ordinal))
        {
            return;
        }

        if (channel.Kind == WorkerWakeupChannelKind.WorkerNamespace)
        {
            var throttle = _throttles.GetOrAdd(channel.Name, static _ => new PublishThrottle());
            var wait = throttle.Claim(PublishWindow);
            if (wait is null)
            {
                // A trailing publish is already scheduled, and it runs after this wake's work committed.
                return;
            }

            if (wait.Value > TimeSpan.Zero)
            {
                _ = PublishTrailingAsync(channel, reason, throttle, wait.Value);
                return;
            }
        }

        await PublishAsync(channel, reason);
    }

    public async ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        await EnsureSubscribedAsync(channel.Kind == WorkerWakeupChannelKind.JobCompletion ? _completions : _wakes, ct);
        return await _local.WaitAsync(channel, timeout, ct);
    }

    private async Task PublishTrailingAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, PublishThrottle throttle, TimeSpan wait)
    {
        await Task.Delay(wait);

        // Open the next window BEFORE publishing: a wake arriving from here on schedules a trailing
        // publish of its own. Opened afterwards, a wake landing between this publish and the reopening
        // would find a trailing publish still marked and be dropped, with nothing left to carry it.
        throttle.Restart();
        await PublishAsync(channel, reason);
    }

    private async Task PublishAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason)
    {
        Interlocked.Increment(ref _published);
        try
        {
            // FireAndForget: a wake is best-effort by contract, so delivery is never awaited and a
            // down Redis surfaces nowhere on this path. The message carries only the reason tag for
            // wire-level debugging; receivers do not read it (routing is the channel name).
            await _redis
                .GetSubscriber()
                .PublishAsync(
                    RedisChannel.Literal(
                        (channel.Kind == WorkerWakeupChannelKind.JobCompletion ? _donePrefix : _wakePrefix) + channel.Name
                    ),
                    WorkerWakeupPublisher.ReasonTag(reason),
                    CommandFlags.FireAndForget
                );
        }
        catch (Exception ex)
        {
            ActaLog.RedisPublishFailed(_log, ex, channel.Name);
        }
    }

    /// <summary>
    /// Each route's pattern subscription is established lazily by its first waiter; publish-only
    /// processes never subscribe. A subscription raced by Redis downtime resubscribes with the
    /// multiplexer's reconnect; wakes missed meanwhile are covered by the waiters' poll floors.
    /// </summary>
    private async ValueTask EnsureSubscribedAsync(Subscription subscription, CancellationToken ct)
    {
        if (subscription.Active)
        {
            return;
        }

        await subscription.Gate.WaitAsync(ct);
        try
        {
            if (subscription.Active)
            {
                return;
            }

            await _redis.GetSubscriber().SubscribeAsync(subscription.Pattern, OnRemoteWake);
            subscription.Active = true;
        }
        catch (Exception ex)
        {
            ActaLog.RedisSubscribeFailed(_log, ex);
        }
        finally
        {
            subscription.Gate.Release();
        }
    }

    /// <summary>Relays one message a subscription received. Internal so a test can deliver one without a server.</summary>
    internal void OnRemoteWake(RedisChannel redisChannel, RedisValue message)
    {
        var name = (string?)redisChannel;
        var suffix =
            name is null ? null
            : name.StartsWith(_wakePrefix, StringComparison.Ordinal) ? name[_wakePrefix.Length..]
            : name.StartsWith(_donePrefix, StringComparison.Ordinal) ? name[_donePrefix.Length..]
            : null;
        if (!WorkerWakeupChannel.TryParse(suffix, out var channel))
        {
            return;
        }

        // Fire-and-forget: the relay must never block the subscriber thread. Worker-namespace wakes
        // from other processes are jittered so the fleet doesn't stampede the claim index off one
        // enqueue; job-completion wakes deliver immediately (single waiter, latency-priority).
        if (channel.Kind == WorkerWakeupChannelKind.JobCompletion || _remoteJitterMax <= TimeSpan.Zero)
        {
            _ = DeliverAsync(channel, TimeSpan.Zero);
            return;
        }

        // The useful state is one pending wake per channel, so coalesce BEFORE allocating anything: a
        // duplicate arriving while a delayed wake is already scheduled is dropped, because that wake
        // will serve it. A burst costs one timer per channel instead of one per message.
        if (!_pendingJittered.TryAdd(channel, 0))
        {
            return;
        }

        Interlocked.Increment(ref _jitteredWakesScheduled);
        _ = DeliverAsync(channel, TimeSpan.FromTicks(JitterSource.NextInt64(_remoteJitterMax.Ticks + 1)));
    }

    private async Task DeliverAsync(WorkerWakeupChannel channel, TimeSpan jitter)
    {
        try
        {
            if (jitter > TimeSpan.Zero)
            {
                await Task.Delay(jitter);
            }

            // Clear the pending message BEFORE waking, never after: a message landing in the gap starts a
            // fresh pending wake, which is one redundant wake onto a latch. Clearing afterwards
            // would instead drop a message that this wake had already passed, losing it outright.
            // A zero draw clears it too, or every later wake on the channel would be dropped.
            _pendingJittered.TryRemove(channel, out _);
            await _local.WakeAsync(channel, WorkerWakeupReason.Unknown);
        }
        catch (Exception ex)
        {
            _pendingJittered.TryRemove(channel, out _);
            ActaLog.RedisRelayFailed(_log, ex, channel.Name);
        }
    }

    /// <summary>
    /// Both dispose shapes: hosting disposes the container asynchronously, but plain
    /// ServiceProvider.Dispose() is synchronous and throws on an IAsyncDisposable-only singleton.
    /// </summary>
    public void Dispose()
    {
        Unsubscribe(_wakes);
        Unsubscribe(_completions);
        _wakes.Dispose();
        _completions.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await UnsubscribeAsync(_wakes);
        await UnsubscribeAsync(_completions);
        _wakes.Dispose();
        _completions.Dispose();
    }

    private void Unsubscribe(Subscription subscription)
    {
        if (!subscription.Active)
        {
            return;
        }

        try
        {
            _redis.GetSubscriber().Unsubscribe(subscription.Pattern);
        }
        catch (Exception)
        {
            // Best effort: the process is going away, and the server drops the subscription with the connection.
        }
    }

    private async ValueTask UnsubscribeAsync(Subscription subscription)
    {
        if (!subscription.Active)
        {
            return;
        }

        try
        {
            await _redis.GetSubscriber().UnsubscribeAsync(subscription.Pattern);
        }
        catch (Exception)
        {
            // Best effort, as in Unsubscribe.
        }
    }

    /// <summary>One route's pattern subscription and the gate that makes it once.</summary>
    private sealed class Subscription(RedisChannel pattern) : IDisposable
    {
        public readonly RedisChannel Pattern = pattern;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile bool Active;

        public void Dispose() => Gate.Dispose();
    }

    /// <summary>One namespace's publish window: when this process last published its wake, and whether a trailing publish is due.</summary>
    private sealed class PublishThrottle
    {
        private readonly System.Threading.Lock _gate = new();
        private long _publishedAt;
        private bool _trailing;

        /// <summary>
        /// Zero to publish now, the wait until the window ends when this wake schedules the trailing
        /// publish, or null when one is already scheduled.
        /// </summary>
        public TimeSpan? Claim(TimeSpan window)
        {
            lock (_gate)
            {
                if (_trailing)
                {
                    return null;
                }

                var now = Stopwatch.GetTimestamp();
                var since = Stopwatch.GetElapsedTime(_publishedAt, now);
                if (since >= window)
                {
                    _publishedAt = now;
                    return TimeSpan.Zero;
                }

                _trailing = true;
                return window - since;
            }
        }

        /// <summary>Starts the next window at the trailing publish.</summary>
        public void Restart()
        {
            lock (_gate)
            {
                _trailing = false;
                _publishedAt = Stopwatch.GetTimestamp();
            }
        }
    }
}
