using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta;

/// <summary>
/// Delivers an alert to a Slack incoming webhook (<c>transport_kind = "slack-webhook"</c>): POSTs the
/// <see cref="SlackAlertFormatter"/> payload to the channel's <c>endpoint</c> URL. Maps HTTP outcomes to
/// retry semantics: 2xx delivered, 429/5xx retryable, other 4xx permanent. No external dependency (BCL
/// <see cref="HttpClient"/> and origin-generated JSON).
/// </summary>
internal sealed class SlackAlertTransport(HttpClient? http = null, ILogger<SlackAlertTransport>? log = null) : IAlertTransport
{
    /// <summary>App-lifetime singleton transport uses one long-lived HttpClient (the documented singleton pattern).</summary>
    private readonly HttpClient _http = http ?? new HttpClient();
    private readonly ILogger _log = log ?? NullLogger<SlackAlertTransport>.Instance;

    /// <summary>The <c>transport_kind</c> this transport handles.</summary>
    public const string Kind = AlertTransportKinds.SlackWebhook;

    public string TransportKind => Kind;

    public async Task<AlertSendStatus> SendAsync(AlertNotification notification, AlertTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Endpoint))
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                ActaLog.SlackChannelHasNoEndpoint(_log, target.ChannelName, notification.AlertRef.ToString());
            }
            return AlertSendStatus.Permanent;
        }

        try
        {
            var payload = SlackAlertFormatter.Build(notification);
            var json = JsonSerializer.Serialize(payload, AlertSlackJsonContext.Default.SlackMessage);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(target.Endpoint, content, ct);

            if (response.IsSuccessStatusCode)
            {
                return AlertSendStatus.Delivered;
            }

            var status = (int)response.StatusCode;
            return status is 429 or >= 500 ? AlertSendStatus.Retryable : AlertSendStatus.Permanent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            if (_log.IsEnabled(LogLevel.Warning))
            {
                ActaLog.SlackDeliveryFailed(_log, ex, target.ChannelName, notification.AlertRef.ToString());
            }
            return AlertSendStatus.Retryable;
        }
    }
}

/// <summary>
/// Builds the Slack incoming-webhook payload for an alert: a header line with a severity emoji and title,
/// the message body, and a colored attachment carrying the alert ref, kind, namespace, the subject job's
/// ref when there is one, occurrences, and runbook. Identity is always a public ref, never a row id.
/// Pure (no I/O); the JSON shape is origin-generated for NativeAOT via <see cref="AlertSlackJsonContext"/>.
/// </summary>
internal static class SlackAlertFormatter
{
    public static SlackMessage Build(AlertNotification n)
    {
        var (emoji, color) = n.Severity switch
        {
            AlertSeverityCode.Critical => ("\U0001F6A8", "#b00020"),
            AlertSeverityCode.Error => ("❌", "#d32f2f"),
            AlertSeverityCode.Warning => ("⚠️", "#f9a825"),
            _ => ("ℹ️", "#2962ff"),
        };

        // Alert first, and unconditional: it is the one handle that addresses what paged the reader.
        // The Job field is optional because an alert can have no subject job, which used to leave such
        // a notification with no addressable identity at all.
        var fields = new List<SlackField>
        {
            new("Alert", n.AlertRef.ToString(), true),
            new("Kind", n.Kind.ToString(), true),
            new("Namespace", n.JobNamespace, true),
        };
        if (n.JobRef is { } jobRef)
        {
            fields.Add(new("Job", jobRef.ToString(), true));
        }
        if (n.OccurrenceCount > 1)
        {
            fields.Add(new("Occurrences", n.OccurrenceCount.ToString(CultureInfo.InvariantCulture), true));
        }
        if (!string.IsNullOrEmpty(n.RunbookUrl))
        {
            fields.Add(new("Runbook", n.RunbookUrl, false));
        }

        var text = $"{emoji} *{n.Title}*\n{n.Message}";
        return new SlackMessage(text, [new SlackAttachment(color, fields)]);
    }
}

/// <summary>
/// Origin-generated JSON metadata for the Slack payload; keeps Slack delivery reflection-free under
/// NativeAOT.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SlackMessage))]
internal sealed partial class AlertSlackJsonContext : JsonSerializerContext;
