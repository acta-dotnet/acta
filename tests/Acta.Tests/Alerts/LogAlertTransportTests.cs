using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Alerts;

public sealed class LogAlertTransportTests
{
    private static readonly AlertNotification Notification = new(
        AlertRef: new AlertRef(new Guid("019826f0-0000-7000-8000-000000000001")),
        JobNamespace: "orders",
        JobRef: new JobRef(new Guid("019826f0-0000-7000-8000-00000000002a")),
        Severity: AlertSeverityCode.Error,
        Kind: AlertKindCode.FinalFailure,
        Title: "Job failed",
        Message: "boom",
        RunbookUrl: "https://runbook.example/job-failed",
        OccurrenceCount: 2,
        CreatedAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
    );

    private static readonly AlertTarget Target = new(
        ChannelName: "default",
        TransportKind: LogAlertTransport.Kind,
        Endpoint: "",
        ConfigFormatId: 0,
        Config: ReadOnlyMemory<byte>.Empty
    );

    [Fact]
    public async Task SendAsync_logs_runbook_url_when_present()
    {
        var logger = new RecordingLogger();
        var transport = new LogAlertTransport(logger);

        var outcome = await transport.SendAsync(Notification, Target, CancellationToken.None);

        Assert.Equal(AlertSendStatus.Delivered, outcome);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("https://runbook.example/job-failed", entry.Message);

        // The runbook link is part of the delivered body, which the canonical log vocabulary carries in the
        // one free-text ActaDetail field; what matters is that a property-rendering sink still receives it.
        Assert.Contains(entry.Properties, p =>
            p.Key == "ActaDetail" && ((string?)p.Value)?.Contains("https://runbook.example/job-failed", StringComparison.Ordinal) == true
        );
    }

    [Fact]
    public async Task SendAsync_writes_the_brand_as_Acta_and_no_runbook_when_there_is_none()
    {
        var logger = new RecordingLogger();

        await new LogAlertTransport(logger).SendAsync(Notification with { RunbookUrl = null }, Target, CancellationToken.None);

        Assert.Equal(
            "Acta alert ns=orders alert=alr_019826f0000070008000000000000001 job=job_019826f000007000800000000000002a "
                + "kind=final-failure: (x2 [Error] ch=default Job failed - boom)",
            Assert.Single(logger.Entries).Message
        );
    }

    // A log rule routes on the kind as the API, the events, and the views spell it.
    [Theory]
    [InlineData(AlertKindCode.FirstFailure, "first-failure")]
    [InlineData(AlertKindCode.ThresholdReached, "threshold-reached")]
    [InlineData(AlertKindCode.FinalFailure, "final-failure")]
    [InlineData(AlertKindCode.Manual, "manual")]
    public async Task SendAsync_logs_the_kind_in_its_wire_spelling(AlertKindCode kind, string wire)
    {
        var logger = new RecordingLogger();

        await new LogAlertTransport(logger).SendAsync(Notification with { Kind = kind }, Target, CancellationToken.None);

        Assert.Contains(Assert.Single(logger.Entries).Properties, p => p.Key == "ActaAlertKind" && (string?)p.Value == wire);
    }

    private sealed class RecordingLogger : ILogger<LogAlertTransport>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add(new Entry(logLevel, formatter(state, exception), [.. properties]));
        }
    }

    private sealed record Entry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Properties);
}
