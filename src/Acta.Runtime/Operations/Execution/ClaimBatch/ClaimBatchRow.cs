namespace Acta.Runtime;

/// <summary>One row of the claim scan; the horizon sentinel row carries a null <see cref="JobId"/>.</summary>
[DbProjection]
internal sealed record ClaimBatchRow(
    long? JobId,
    int? NamespaceId,
    int? DefinitionId,
    int? ExecutionNumber,
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ConcurrencyKey,
    byte? InputFormatId,
    byte[]? Input,
    DateTime? NextRunAtUtc,
    DateTime? LeaseExpiresAtUtc,
    DateTime? CreatedAtUtc,
    int? FailureCount,
    int? Version,
    Guid? JobRef,
    int? TenantId,
    DateTime? DbNowUtc,
    DateTime? NextReadyAtUtc
) : IClaimColumns;
