using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Lifecycle status of a declared alert channel. Numeric bands are a readability convention only;
/// behavior follows the explicit members.
/// </summary>
[JsonConverter(typeof(AlertChannelStatusCodeJsonConverter))]
[CodeKind("alert-channel-status")]
public enum AlertChannelStatusCode : byte
{
    [Code("active", "Channel accepts deliveries.")]
    Active = 10,

    [Code("disabled", "Configured but intentionally muted; matching alerts are suppressed.")]
    Disabled = 30,

    [Code("deprecated", "Configured but decommissioned; matching alerts are suppressed.")]
    Deprecated = 240,
}
