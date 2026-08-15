namespace Acta;

/// <summary>
/// One Quarantined staging row as the operator listing reports it: identity and routing fields plus
/// the failure evidence. The payload never leaves the producer's database.
/// </summary>
/// <param name="OutboxId">The staging row's producer-minted id; the target of a scoped requeue/discard.</param>
/// <param name="JobNamespace">The route's target namespace carried on the row.</param>
/// <param name="JobName">The route's target job name carried on the row.</param>
/// <param name="DeduplicationKey">The row's ledger deduplication key.</param>
/// <param name="CorrelationKey">The producer's correlation key; null when none was set.</param>
/// <param name="TenantKey">The row's tenant key; null for a tenant-less row.</param>
/// <param name="FailureCount">Delivery attempts consumed before quarantine.</param>
/// <param name="LastError">The bounded last delivery error, kept as evidence.</param>
/// <param name="CreatedAtUtc">When the producer staged the row.</param>
public sealed record OutboxQuarantinedItem(
    Guid OutboxId,
    string JobNamespace,
    string JobName,
    string DeduplicationKey,
    string? CorrelationKey,
    string? TenantKey,
    int FailureCount,
    string? LastError,
    DateTime CreatedAtUtc
);
