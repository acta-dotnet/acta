using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RuntimeJobContextLockReleaseTests
{
    [Fact]
    public async Task Concurrency_slot_release_failure_is_logged_and_does_not_escape()
    {
        // The slot acquire takes slot 0; the release fails in the database.
        var session = new ScriptedSession(call =>
            call.Stem == "AcquireSlot" ? $"{call.Get(ActaSchema.Sql.LockKeyPrefix)}.0" : throw new TimeoutException("release failed")
        );
        var logger = new RecordingLogger();
        var ctx = CreateContext(session.Services().GetRequiredService<LocksOperations>(), logger);
        Assert.True(await ctx.TryAcquireConcurrencySlotAsync("customer-1", limit: 1, CancellationToken.None));

        await ctx.ReleaseConcurrencySlotAsync(CancellationToken.None);

        Assert.Equal(1, session.Calls.Count(call => call.Stem == "ReleaseLock"));
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Levels));
    }

    private static RuntimeJobContext CreateContext(LocksOperations locks, ILogger logger) =>
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
                ConcurrencyKey: "customer-1",
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
            jobOperations: null!,
            signals: null!,
            alerts: null!,
            executionStore: null!,
            new ThrowingSerializerRegistry(),
            locks,
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null,
            log: logger
        );

    private sealed class ThrowingSerializerRegistry : IJobPayloadSerializerRegistry
    {
        public IJobPayloadSerializer Resolve(byte formatId) => throw new NotSupportedException();

        public bool IsRegistered(byte formatId) => false;
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
