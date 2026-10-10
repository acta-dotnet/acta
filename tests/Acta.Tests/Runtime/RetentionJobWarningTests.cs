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
        var store = Purges(section => section == RetentionSection.UndeliveredAlerts ? batches.Dequeue() : 0);

        await CreateJob(store, logger).Handle(new RecordingJobContext(), TestContext.Current.CancellationToken);

        AssertPurgeWarning(logger, 5);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_sweep_warns_about_committed_alert_batches_and_preserves_the_error(bool failInSameSection)
    {
        var logger = new RecordingLogger();
        var batches = new Queue<int>([2, 3, 0]);
        var failure = new InvalidOperationException("failed retention batch");
        var store = Purges(section =>
        {
            if (section == RetentionSection.UndeliveredAlerts)
            {
                if (failInSameSection && batches.Count == 1)
                {
                    throw failure;
                }
                return batches.Dequeue();
            }
            return section == RetentionSection.Workers ? throw failure : 0;
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
    public async Task Cancelled_sweep_warns_about_committed_alert_batches_and_preserves_cancellation(bool cancelInSameSection)
    {
        var logger = new RecordingLogger();
        var batches = new Queue<int>([2, 3, 0]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = Purges(section =>
        {
            if (section == RetentionSection.UndeliveredAlerts)
            {
                var deleted = batches.Dequeue();
                if (cancelInSameSection && batches.Count == 1)
                {
                    cancellation.Cancel();
                }
                return deleted;
            }
            if (section == RetentionSection.Workers)
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
        Assert.Contains($"purged {count} alerts", record.Message, StringComparison.Ordinal);
        Assert.Contains("alert-retention-cap", record.Message, StringComparison.Ordinal);
    }

    private static RetentionJob CreateJob(PurgeExpiredDataHandler purge, ILogger<RetentionJob> logger) =>
        new(new MaintenanceOperations(purge), new StubClock(), Options.Create(new JobsOptions()), logger);

    private static PurgeExpiredDataHandler Purges(Func<RetentionSection, int> execute) =>
        new RetentionJobTests.Store(command => execute(command.Section)).Handler;

    // Each section deletes once and then reports empty: three rows anywhere but the undelivered alerts.
    private static PurgeExpiredDataHandler Stub(int undelivered)
    {
        var visited = new HashSet<RetentionSection>();
        return Purges(section =>
            visited.Add(section)
                ? section == RetentionSection.UndeliveredAlerts
                    ? undelivered
                    : 3
                : 0
        );
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
