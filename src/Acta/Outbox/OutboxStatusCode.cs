using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(OutboxStatusCodeJsonConverter))]
[CodeKind("outbox-status")]
public enum OutboxStatusCode : byte
{
    [Code("pending", "Due for relay claim; the claim path selects rows in this status once next_attempt_at_utc is reached.")]
    Pending = 10,

    [Code("claimed", "Leased by a relay claim; claim_token and claim_until_utc are set until the lease finalizes or expires.")]
    Claimed = 20,

    [Code(
        "quarantined",
        "Excluded from normal claims after exhausting delivery policy or a non-recoverable contract error; requires an operator requeue or delete."
    )]
    Quarantined = 90,
}
