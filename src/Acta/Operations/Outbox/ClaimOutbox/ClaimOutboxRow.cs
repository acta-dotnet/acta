namespace Acta;

/// <summary>
/// One claimed external-outbox row: the positional projection of a source row and the fields the relay needs to
/// reconstruct an <see cref="EnqueueJobRequest"/> and finalize under the current claim token. The constructor order is
/// the <c>[DbProjection]</c> contract and must match the SELECT column order in every dialect's <c>ClaimOutbox</c> SQL
/// at every position. Binary is read as <c>byte[]?</c> like the ledger input projections; <c>PriorityCode</c> stays
/// null when the producer set no override, and so does the reconstructed request; <c>Id</c> is the producer insert
/// order (the identity key, the rowid on SQLite) the claim and the relay batch follow.
/// </summary>
[DbProjection]
internal sealed record ClaimOutboxRow(
    Guid OutboxId,
    string JobNamespace,
    string JobName,
    byte InputFormatId,
    byte[]? Input,
    string DeduplicationKey,
    string? CorrelationKey,
    string? ConcurrencyKey,
    string? Lane,
    byte? PriorityCode,
    DateTime? NextRunAtUtc,
    int? DelaySeconds,
    string? TenantKey,
    string? MetaJson,
    DateTime CreatedAtUtc,
    int FailureCount,
    long Id
);
