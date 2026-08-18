using Acta.Runtime.Modules.Alerting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.Alerts;

public sealed class LogAlertTransportTests
{
    [Fact]
    public async Task SendAsync_logs_runbook_url_when_present()
    {
        var logger = new RecordingLogger();
        var transport = new LogAlertTransport(logger);
        var notification = new AlertNotification(
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
        var target = new AlertTarget(
            ChannelName: "default",
            TransportKind: LogAlertTransport.Kind,
            Endpoint: "",
            ConfigFormatId: 0,
            Config: ReadOnlyMemory<byte>.Empty
        );

        var outcome = await transport.SendAsync(notification, target, CancellationToken.None);

        Assert.Equal(AlertDeliveryOutcome.Delivered, outcome);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("https://runbook.example/job-failed", entry.Message);

        // The runbook link is part of the delivered body, which the canonical log vocabulary carries in the
        // one free-text Detail field; what matters is that a property-rendering sink still receives it.
        Assert.Contains(
            entry.Properties,
            p => p.Key == "Detail" && ((string?)p.Value)?.Contains("https://runbook.example/job-failed", StringComparison.Ordinal) == true
        );
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
