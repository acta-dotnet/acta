namespace Acta;

/// <summary>
/// One Quarantined staging row as the operator listing reports it: identity and routing fields (a null
/// correlation or tenant key was never set), when the producer staged it, and the failure evidence
/// (<c>FailureCount</c> delivery attempts consumed before quarantine, and the bounded <c>LastError</c>).
/// <c>OutboxId</c> is the producer-minted id a scoped requeue or discard targets. The payload never leaves
/// the producer's database.
/// </summary>
public sealed record ListOutboxQuarantinedItem(
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
