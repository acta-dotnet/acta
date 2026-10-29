namespace Acta;

/// <summary>One row of the claim scan; the horizon sentinel row carries a null <see cref="JobId"/>.</summary>
[DbProjection]
internal sealed record ClaimJobsRow(
    long? JobId,
    int? DefinitionId,
    int? ExecutionNumber,
    string? DeduplicationKey,
    JobKindCode? JobKindCode,
    string? CorrelationKey,
    string? ConcurrencyKey,
    byte? InputFormatId,
    byte[]? Input,
    DateTime? NextRunAtUtc,
    DateTime? CreatedAtUtc,
    int? FailureCount,
    Guid? JobRef,
    int? TenantId,
    DateTime? DbNowUtc,
    DateTime? NextReadyAtUtc,
    long? LaneId,
    long? ParentJobId
) : IClaimColumns;
