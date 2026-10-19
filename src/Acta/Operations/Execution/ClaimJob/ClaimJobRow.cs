namespace Acta;

/// <summary>The claimed row of a by-id claim: the claim scan's columns, in its order, without the horizon.</summary>
[DbProjection]
internal sealed record ClaimJobRow(
    long? JobId,
    int? DefinitionId,
    int? ExecutionNumber,
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ConcurrencyKey,
    byte? InputFormatId,
    byte[]? Input,
    DateTime? NextRunAtUtc,
    DateTime? CreatedAtUtc,
    int? FailureCount,
    Guid? JobRef,
    int? TenantId,
    long? LaneId,
    long? ParentId
) : IClaimColumns;
