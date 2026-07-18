using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(AlertChannelStatusCodeJsonConverter))]
[CodeKind("alert-channel-status")]
public enum AlertChannelStatusCode : byte
{
    // Numeric bands are a readability convention only; behavior matches explicit members.
    [Code("active", "Channel accepts deliveries.")]
    Active = 10,

    [Code("disabled", "Configured but intentionally muted; matching alerts are suppressed.")]
    Disabled = 30,

    [Code("deprecated", "Configured but decommissioned; matching alerts are suppressed.")]
    Deprecated = 240,
}
