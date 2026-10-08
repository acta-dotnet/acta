namespace Acta;

/// <summary>The claimed row of a by-id claim, in the claim scan's column order; its horizon columns are always null.</summary>
[DbProjection]
internal sealed record ClaimOneRow(
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
