namespace Acta;

/// <summary>
/// The canonical, caller-supplied columns of one staged external-outbox row, projected from a validated
/// <see cref="EnqueueJobRequest"/> by <see cref="OutboxStaging"/>. Carries only what the producer
/// writes: <c>outbox_id</c> is the client-generated GUID and the operational columns
/// (<c>created_at_utc</c>, <c>status_code</c>, <c>failure_count</c>, <c>next_attempt_at_utc</c>, the claim
/// pair, <c>last_error</c>) are omitted so the database defaults apply. Each provider staging extension
/// binds these fields into its own canonical INSERT.
/// </summary>
internal readonly record struct OutboxStagingValues(
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
    string? TenantName,
    string? Meta
);
