namespace Acta.Runtime;

/// <summary>
/// Token-CAS transition of claimed rows to Quarantined, one <see cref="OutboxQuarantine"/> per row so a
/// coalesced group can partially quarantine: each row records its own consumed failure count and error.
/// </summary>
internal sealed record QuarantineOutboxCommand(Guid ClaimToken, IReadOnlyList<OutboxQuarantine> Rows);
