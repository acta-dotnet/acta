namespace Acta.Runtime;

/// <summary>Token-CAS release of the claimed rows named back to Pending; nothing records a count or error.</summary>
internal sealed record ReleaseClaimedOutboxCommand(Guid ClaimToken, IReadOnlyList<Guid> OutboxIds);
