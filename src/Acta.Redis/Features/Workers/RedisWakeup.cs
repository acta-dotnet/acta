using Acta.Features.Workers;
using Acta.Redis.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Acta.Redis.Features.Workers;

/// <summary>
/// Redis-backed <see cref="IWorkerWakeup"/>: composes the in-process transport for local delivery and
/// relays wakes across processes via Redis pub/sub. A wake publishes locally FIRST (the publishing
/// process's waiters never wait on a network round trip), then fire-and-forgets one Redis message
/// on <c>{prefix}:wake:{channel.Name}</c>; every subscribed process delivers it into its own local
/// transport. Redis being down, slow, or lossy costs latency only: publishes are fire-and-forget,
/// the multiplexer reconnects on its own, and every waiter keeps its poll floor.
/// </summary>
/// <remarks>
/// The pattern subscription also receives this process's own publishes; that loopback re-wake
/// coalesces onto the local latch and is harmless. Channel semantics are preserved across the
/// relay: a remote job-completion wake reaches existing waiters only (the local transport never
/// allocates for it), and a remote worker-namespace wake is jittered per
/// <see cref="RedisWakeupOptions.RemoteWakeJitterMax"/> to soften the fleet-wide claim herd.
/// </remarks>
internal sealed class RedisWakeup : IWorkerWakeup, IDisposable, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly InProcessWakeup _local = new();
    private readonly string _channelPrefix;
    private readonly RedisChannel _wakePattern;
    private readonly TimeSpan _remoteJitterMax;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _subscribeGate = new(1, 1);
    private volatile bool _subscribed;

    public RedisWakeup(IConnectionMultiplexer redis, IOptions<RedisWakeupOptions> options, ILogger<RedisWakeup>? log = null)
    {
        _redis = redis;
        _channelPrefix = options.Value.ChannelPrefix + ":wake:";
        _wakePattern = RedisChannel.Pattern(_channelPrefix + "*");
        _remoteJitterMax = options.Value.RemoteWakeJitterMax;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public async ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
    {
        // Local first: the publishing process's own waiters wake without a network round trip even
        // when Redis is unreachable.
        await _local.WakeAsync(channel, reason, ct);

        try
        {
            // FireAndForget: a wake is best-effort by contract, so delivery is never awaited and a
            // down Redis surfaces nowhere on this path. The message carries only the reason tag for
            // wire-level debugging; receivers do not read it (routing is the channel name).
            await _redis
                .GetSubscriber()
                .PublishAsync(
                    RedisChannel.Literal(_channelPrefix + channel.Name),
                    WorkerWakeupPublisher.ReasonTag(reason),
                    CommandFlags.FireAndForget
                );
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Redis wake publish failed for '{Channel}'; remote waiters fall back to their poll floors.", channel.Name);
        }
    }

    public async ValueTask<WorkerWakeupWaitResult> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
    {
        await EnsureSubscribedAsync(ct);
        return await _local.WaitAsync(channel, timeout, ct);
    }

    // One pattern subscription per process, established lazily by the first waiter; publish-only
    // processes never subscribe. A subscription raced by Redis downtime resubscribes with the
    // multiplexer's reconnect; wakes missed meanwhile are covered by the waiters' poll floors.
    private async ValueTask EnsureSubscribedAsync(CancellationToken ct)
    {
        if (_subscribed)
        {
            return;
        }

        await _subscribeGate.WaitAsync(ct);
        try
        {
            if (_subscribed)
            {
                return;
            }

            await _redis.GetSubscriber().SubscribeAsync(_wakePattern, OnRemoteWake);
            _subscribed = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "Redis wake subscription failed; this process wakes on its poll floors until a wait retries the subscription."
            );
        }
        finally
        {
            _subscribeGate.Release();
        }
    }

    private void OnRemoteWake(RedisChannel redisChannel, RedisValue message)
    {
        var name = (string?)redisChannel;
        if (
            name is null
            || name.Length <= _channelPrefix.Length
            || !WorkerWakeupChannel.TryParse(name[_channelPrefix.Length..], out var channel)
        )
        {
            return;
        }

        // Fire-and-forget: the relay must never block the subscriber thread. Worker-namespace wakes
        // from other processes are jittered so the fleet doesn't stampede the claim index off one
        // enqueue; job-completion wakes deliver immediately (single waiter, latency-priority).
        var jitter =
            channel.Kind == WorkerWakeupChannelKind.JobCompletion || _remoteJitterMax <= TimeSpan.Zero
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(Random.Shared.NextInt64(_remoteJitterMax.Ticks + 1));
        _ = DeliverAsync(channel, jitter);
    }

    private async Task DeliverAsync(WorkerWakeupChannel channel, TimeSpan jitter)
    {
        try
        {
            if (jitter > TimeSpan.Zero)
            {
                await Task.Delay(jitter);
            }

            await _local.WakeAsync(channel, WorkerWakeupReason.Unknown);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Redis wake relay failed for '{Channel}'.", channel.Name);
        }
    }

    // Both dispose shapes: hosting disposes the container asynchronously, but plain
    // ServiceProvider.Dispose() is synchronous and throws on an IAsyncDisposable-only singleton.
    public void Dispose()
    {
        if (_subscribed)
        {
            try
            {
                _redis.GetSubscriber().Unsubscribe(_wakePattern);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Redis wake unsubscribe failed during dispose.");
            }
        }

        _subscribeGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribed)
        {
            try
            {
                await _redis.GetSubscriber().UnsubscribeAsync(_wakePattern);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Redis wake unsubscribe failed during dispose.");
            }
        }

        _subscribeGate.Dispose();
    }
}
