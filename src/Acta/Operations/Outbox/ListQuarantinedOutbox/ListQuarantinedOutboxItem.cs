namespace Acta;

/// <summary>
/// One quarantined outbox row: what it would enqueue, when it was staged, and why relaying it failed. Its payload stays in the producer's
/// database.
/// </summary>
/// <param name="OutboxId">The quarantined row's id.</param>
/// <param name="JobNamespace">The namespace of the job the row would enqueue.</param>
/// <param name="JobName">The name of the definition the row would enqueue.</param>
/// <param name="DeduplicationKey">The deduplication key the row would enqueue with.</param>
/// <param name="CorrelationKey">The correlation key the row would enqueue with, when it has one.</param>
/// <param name="TenantName">The tenant the job is about, when it names one.</param>
/// <param name="FailureCount">How many times relaying the row failed.</param>
/// <param name="LastError">The last relay error; null when none was recorded.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
public sealed record ListQuarantinedOutboxItem(
    Guid OutboxId,
    string JobNamespace,
    string JobName,
    string DeduplicationKey,
    string? CorrelationKey,
    string? TenantName,
    int FailureCount,
    string? LastError,
    DateTime CreatedAtUtc
);
