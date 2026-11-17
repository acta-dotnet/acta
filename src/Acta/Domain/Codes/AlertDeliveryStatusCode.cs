using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Where an alert stands with its delivery to its channel.</summary>
/// <remarks>Numeric bands are a readability convention only; behavior follows the explicit members.</remarks>
[JsonConverter(typeof(AlertDeliveryStatusCodeJsonConverter))]
[CodeKind("alert-delivery-status")]
public enum AlertDeliveryStatusCode : byte
{
    [Code("unspecified", "Alert delivery status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("pending", "Raised; awaiting the first send attempt.")]
    Pending = 10,

    [Code("retrying", "A send failed and the next one is due at the alert's retryAfterUtc.")]
    Retrying = 20,

    [Code("delivered", "Sent to the channel's transport.")]
    Delivered = 100,

    [Code(
        "suppressed",
        "Intentionally not sent: the channel's policy on the sending process rejected delivery, or the alert was resolved before its send."
    )]
    Suppressed = 150,

    [Code("failed", "Send attempt failed and the retry path is done with it; re-sent as a reminder while the incident stays unresolved.")]
    Failed = 200,
}
