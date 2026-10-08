namespace Acta;

/// <summary>Token-CAS delete of the claimed rows named; a row whose token moved is skipped, and nothing records a count or error.</summary>
internal sealed record DeleteClaimedOutboxCommand(Guid ClaimToken, IReadOnlyList<Guid> OutboxIds);
