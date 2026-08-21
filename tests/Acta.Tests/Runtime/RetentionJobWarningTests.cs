using Acta.Runtime.Maintenance;
using Acta.Tests.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The maintenance pass's one-warning rule, at the seam a conformance spec cannot reach: an alert that
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

        await CreateJob(new StubRetentionStore(undelivered: 250), logger).Handle(new RecordingJobContext(), ct);

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
        await CreateJob(new StubRetentionStore(undelivered: 0), logger).Handle(new RecordingJobContext(), ct);

        Assert.Empty(logger.Records);
    }

    private static RetentionJob CreateJob(IRetentionStore store, ILogger<RetentionJob> logger) =>
        new(store, Options.Create(new JobsOptions()), logger);

    private sealed class StubRetentionStore(int undelivered) : IRetentionStore
    {
        public Task<PurgeExpiredDataResult> PurgeExpiredDataAsync(PurgeExpiredDataCommand command, CancellationToken ct) =>
            Task.FromResult(
                new PurgeExpiredDataResult(Jobs: 7, Events: 9, Alerts: 3, UndeliveredAlertsPurged: undelivered, Workers: 1, Locks: 2)
            );
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
