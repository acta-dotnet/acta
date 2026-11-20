using Acta.Tests.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The maintenance pass's one-warning rule, where a conformance spec cannot reach: an alert that
/// aged out before delivery settled is a signal an operator never got, and the pass says so once per
/// pass rather than once per row.
/// </summary>
public sealed class RetentionJobWarningTests
{
    [Fact]
    public async Task Undelivered_alerts_purged_warns_exactly_once_however_many_rows_went()
    {
        var logger = new RecordingLogger();
        var ct = TestContext.Current.CancellationToken;

        await CreateJob(Stub(undelivered: 250), logger).Handle(new RecordingJobContext(), ct);

        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("alert-retention-cap", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pass_that_purged_no_undelivered_alert_logs_nothing()
    {
        var logger = new RecordingLogger();
        var ct = TestContext.Current.CancellationToken;

        // Rows went in every other section: the warning is about the undelivered count alone, so a
        // busy-but-healthy pass stays silent.
        await CreateJob(Stub(undelivered: 0), logger).Handle(new RecordingJobContext(), ct);

        Assert.Empty(logger.Records);
    }

    [Fact]
    public async Task Multiple_committed_alert_batches_produce_one_warning_with_the_total()
    {
        var logger = new RecordingLogger();
        var batches = new Queue<int>([2, 3, 0]);
        var store = Purges(stem => stem == RetentionJobTests.Alerts ? batches.Dequeue() : 0);

        await CreateJob(store, logger).Handle(new RecordingJobContext(), TestContext.Current.CancellationToken);

        AssertPurgeWarning(logger, 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_sweep_warns_about_committed_alert_batches_and_preserves_the_error(bool failInSameDelete)
    {
        var logger = new RecordingLogger();
        var batches = new Queue<int>([2, 3, 0]);
        var failure = new InvalidOperationException("failed retention batch");
        var store = Purges(stem =>
        {
            if (stem == RetentionJobTests.Alerts)
            {
                if (failInSameDelete && batches.Count == 1)
                {
                    throw failure;
                }
                return batches.Dequeue();
            }
            return stem == RetentionJobTests.Workers ? throw failure : 0;
        });

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateJob(store, logger).Handle(new RecordingJobContext(), TestContext.Current.CancellationToken)
        );

        Assert.Same(failure, caught);
        AssertPurgeWarning(logger, 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelled_sweep_warns_about_committed_alert_batches_and_preserves_cancellation(bool cancelInSameDelete)
    {
        var logger = new RecordingLogger();
        var batches = new Queue<int>([2, 3, 0]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = Purges(stem =>
        {
            if (stem == RetentionJobTests.Alerts)
            {
                var deleted = batches.Dequeue();
                if (cancelInSameDelete && batches.Count == 1)
                {
                    cancellation.Cancel();
                }
                return deleted;
            }
            if (stem == RetentionJobTests.Workers)
            {
                cancellation.Cancel();
            }
            return 0;
        });

        var caught = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateJob(store, logger).Handle(new RecordingJobContext(), cancellation.Token)
        );

        Assert.Equal(cancellation.Token, caught.CancellationToken);
        AssertPurgeWarning(logger, 5);
    }

    [Fact]
    public async Task Failure_before_any_alert_batch_commits_logs_no_purge_warning()
    {
        var logger = new RecordingLogger();
        var store = Purges(static _ => throw new InvalidOperationException("failed retention batch"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateJob(store, logger).Handle(new RecordingJobContext(), TestContext.Current.CancellationToken)
        );

        Assert.Empty(logger.Records);
    }

    private static void AssertPurgeWarning(RecordingLogger logger, int count)
    {
        var record = Assert.Single(logger.Records);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains($"purged ({count} alerts)", record.Message, StringComparison.Ordinal);
        Assert.Contains("alert-retention-cap", record.Message, StringComparison.Ordinal);
    }

    private static RetentionJob CreateJob(MaintenanceOperations maintenance, ILogger<RetentionJob> logger) =>
        new(maintenance, new StubClock(), Options.Create(new JobsOptions()), logger);

    // Every alert these deletes take is undelivered.
    private static MaintenanceOperations Purges(Func<string, int> execute) =>
        new RetentionJobTests.Store(call => execute(call.Stem), undelivered: static deleted => deleted).Handler;

    // Each delete takes three rows once and then reports empty; the alerts delete takes three settled alerts beside the
    // undelivered ones.
    private static MaintenanceOperations Stub(int undelivered)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return new RetentionJobTests.Store(
            call =>
                visited.Add(call.Stem)
                    ? call.Stem == RetentionJobTests.Alerts
                        ? undelivered + 3
                        : 3
                    : 0,
            undelivered: deleted => deleted > 0 ? undelivered : 0
        ).Handler;
    }

    private sealed class StubClock : IClockOperations
    {
        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(DateTime.UtcNow);
    }

    private sealed class RecordingLogger : ILogger<RetentionJob>
    {
        public List<(LogLevel Level, string Message)> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Records.Add((logLevel, formatter(state, exception)));
    }
}
