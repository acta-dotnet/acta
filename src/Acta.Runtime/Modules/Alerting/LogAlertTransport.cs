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

        // This line is the notification, not a note about one, so both refs stay typed: Ref addresses the
        // alert and SubjectRef the job it concerns, which is the one place a line legitimately carries
        // two entity refs. The rest is the delivered body and renders into the free-text tail.
        _log.Log(
            level,
            "ACTA ALERT ns={Namespace} alert={Ref} job={SubjectRef} kind={Reason} x{Count}: {Detail}",
            n.JobNamespace,
            n.AlertRef,
            n.JobRef,
            n.Kind,
            n.OccurrenceCount,
            $"[{n.Severity}] ch={target.ChannelName} runbook={n.RunbookUrl} {n.Title} - {n.Message}"
        );
        return Task.FromResult(AlertDeliveryOutcome.Delivered);
    }
}
