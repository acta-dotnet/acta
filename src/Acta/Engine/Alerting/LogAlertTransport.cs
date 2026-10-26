using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta;

/// <summary>
/// Zero-dependency transport that writes the alert to the logger and reports success. Always present; the
/// default fallback and the transport the conformance suite delivers through.
/// </summary>
internal sealed class LogAlertTransport(ILogger<LogAlertTransport>? log = null) : IAlertTransport
{
    private readonly ILogger _log = log ?? NullLogger<LogAlertTransport>.Instance;

    /// <summary>The <c>transport_kind</c> this transport handles.</summary>
    public const string Kind = AlertTransportKinds.Log;

    public string TransportKind => Kind;

    public Task<AlertSendStatus> SendAsync(AlertNotification n, AlertTarget target, CancellationToken ct)
    {
        // Map the alert severity onto the log level so Error and Critical alerts survive a pipeline that
        // filters out Information; the log transport is the default fallback, so it must not silently
        // downgrade severity.
        var level = n.Severity switch
        {
            AlertSeverityCode.Critical => LogLevel.Critical,
            AlertSeverityCode.Error => LogLevel.Error,
            AlertSeverityCode.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };

        // This line is the notification, so log-based alert rules route on its fields: the alert, the job it
        // concerns, and the kind that fired. Refs and the kind are rendered here rather than passed as their own
        // types, because a sink that serializes the property object would store the wrapped uuid or the enum's
        // number, a form an operator can neither read nor address. The delivered body is the free-text tail.
        if (_log.IsEnabled(level))
        {
            var alertRef = n.AlertRef.ToString();
            var jobRef = n.JobRef?.ToString();
            var kind = n.Kind.ToString();
            var body = $"x{n.OccurrenceCount} [{n.Severity}] ch={target.ChannelName} runbook={n.RunbookUrl} {n.Title} - {n.Message}";
            ActaLog.AlertNotified(_log, level, n.JobNamespace, alertRef, jobRef, kind, body);
        }
        return Task.FromResult(AlertSendStatus.Delivered);
    }
}
