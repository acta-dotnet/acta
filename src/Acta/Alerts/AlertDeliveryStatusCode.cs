using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(AlertDeliveryStatusCodeJsonConverter))]
[CodeKind("alert-delivery-status")]
public enum AlertDeliveryStatusCode : byte
{
    // Numeric bands are a readability convention only; behavior matches explicit members.
    [Code("pending", "Materialized; awaiting the first send attempt.")]
    Pending = 10,

    [Code("retry-after", "Send deferred until JobAlert.RetryAfterUtc instant.")]
    RetryAfter = 20,

    [Code("suppressed", "Intentionally not sent because process-local channel policy rejected delivery.")]
    Suppressed = 30,

    [Code("delivered", "Successfully sent to the channel transport.")]
    Delivered = 100,

    [Code("failed", "Send attempt failed and the retry path is done with it; re-sent as a reminder while the incident stays unresolved.")]
    Failed = 200,
}
