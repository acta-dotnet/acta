using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RuntimeJobContextLockReleaseTests
{
    [Fact]
    public async Task Exclusive_key_release_failure_is_logged_and_does_not_escape()
    {
        var lockStore = new ReleaseFailureLockStore();
        var logger = new RecordingLogger();
        var ctx = CreateContext(lockStore, logger);
        Assert.True(await ctx.TryAcquireExclusiveKeyLockAsync("customer-1", CancellationToken.None));

        await ctx.ReleaseExclusiveKeyLockAsync(CancellationToken.None);

        Assert.Equal(1, lockStore.ReleaseCalls);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Levels));
    }

    private static RuntimeJobContext CreateContext(ILockStore lockStore, ILogger logger) =>
        new(
            new ClaimedJob(
                JobId: 42,
                JobRef: Guid.CreateVersion7(),
                NamespaceId: 1,
                DefinitionId: 1,
                TenantId: null,
                ExecutionNumber: 1,
                DeduplicationKey: null,
                CorrelationKey: null,
                ExclusiveKey: "customer-1",
                InputFormatId: 0,
                Input: ReadOnlyMemory<byte>.Empty,
                NextRunAtUtc: null,
                LeaseExpiresAtUtc: DateTime.UtcNow.AddMinutes(3),
                CreatedAtUtc: DateTime.UtcNow,
                FailureCount: 0,
                Version: 1
            ),
            jobName: "job",
            namespaceName: "test",
            namespaceId: 1,
            leaseTtlSeconds: 180,
            jobStore: null!,
            signalStore: null!,
            alerts: null!,
            executionStore: null!,
            new ThrowingSerializerRegistry(),
            lockStore,
            new ThrowingClock(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null,
            log: logger
        );

    private sealed class ReleaseFailureLockStore : ILockStore
    {
        public int ReleaseCalls { get; private set; }

        public Task<LockToken?> TryAcquireAsync(string key, TimeSpan ttl, long ownerJobId, CancellationToken ct) =>
            Task.FromResult<LockToken?>(new LockToken(key, 1));

        public Task<bool> ExtendAsync(LockToken token, TimeSpan ttl, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> ReleaseAsync(LockToken token, CancellationToken ct)
        {
            ReleaseCalls++;
            throw new TimeoutException("release failed");
        }
    }

    private sealed class ThrowingSerializerRegistry : IJobPayloadSerializerRegistry
    {
        public IJobPayloadSerializer Resolve(byte formatId) => throw new NotSupportedException();

        public bool IsRegistered(byte formatId) => false;
    }

    private sealed class ThrowingClock : IActaClock
    {
        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Levels.Add(logLevel);
    }
}
