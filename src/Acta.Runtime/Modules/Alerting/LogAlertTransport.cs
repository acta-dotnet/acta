using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta.Runtime.Modules.Alerting;

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

    public Task<AlertDeliveryOutcome> SendAsync(AlertNotification n, AlertTarget target, CancellationToken ct)
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

        _log.Log(
            level,
            "ACTA ALERT [{Severity}/{Kind}] ns={Namespace} job={JobId} ch={Channel} runbook={RunbookUrl} x{Count}: {Title} - {Message}",
            n.Severity,
            n.Kind,
            n.JobNamespace,
            n.JobId,
            target.ChannelName,
            n.RunbookUrl,
            n.OccurrenceCount,
            n.Title,
            n.Message
        );
        return Task.FromResult(AlertDeliveryOutcome.Delivered);
    }
}
