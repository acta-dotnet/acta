using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Delivers an alert to a Slack incoming webhook (<c>transport_kind = "slack-webhook"</c>): POSTs the
/// <see cref="SlackAlertFormatter"/> payload to the channel's <c>endpoint</c> URL. Maps HTTP outcomes to
/// retry semantics: 2xx delivered, 429/5xx retryable, other 4xx permanent. No external dependency (BCL
/// <see cref="HttpClient"/> and origin-generated JSON).
/// </summary>
internal sealed class SlackAlertTransport(HttpClient? http = null, ILogger<SlackAlertTransport>? log = null) : IAlertTransport
{
    // App-lifetime singleton transport uses one long-lived HttpClient (the documented singleton pattern).
    private readonly HttpClient _http = http ?? new HttpClient();
    private readonly ILogger _log = log ?? NullLogger<SlackAlertTransport>.Instance;

    /// <summary>The <c>transport_kind</c> this transport handles.</summary>
    public const string Kind = AlertTransportKinds.SlackWebhook;

    public string TransportKind => Kind;

    public async Task<AlertDeliveryOutcome> SendAsync(AlertNotification notification, AlertTarget target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(target.Endpoint))
        {
            _log.LogWarning(
                "Slack channel '{Detail}' has no endpoint; cannot deliver alert {Ref}.",
                target.ChannelName,
                notification.AlertRef.ToString()
            );
            return AlertDeliveryOutcome.Permanent;
        }

        try
        {
            var payload = SlackAlertFormatter.Build(notification);
            var json = JsonSerializer.Serialize(payload, AlertSlackJsonContext.Default.SlackMessage);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(target.Endpoint, content, ct);

            if (response.IsSuccessStatusCode)
            {
                return AlertDeliveryOutcome.Delivered;
            }

            var status = (int)response.StatusCode;
            return status is 429 or >= 500 ? AlertDeliveryOutcome.Retryable : AlertDeliveryOutcome.Permanent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(
                ex,
                "Slack delivery to channel '{Detail}' failed transiently for alert {Ref}.",
                target.ChannelName,
                notification.AlertRef.ToString()
            );
            return AlertDeliveryOutcome.Retryable;
        }
    }
}
