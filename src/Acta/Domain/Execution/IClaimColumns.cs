namespace Acta;

/// <summary>
/// The claimed-job columns both claim statements return, so <see cref="ClaimedJob.From"/> reads a <c>ClaimJobs</c> or a
/// <c>ClaimJob</c> row. <see cref="JobId"/> is null only on the claim scan's empty-claim horizon sentinel row.
/// </summary>
internal interface IClaimColumns
{
    long? JobId { get; }

    int? DefinitionId { get; }

    int? ExecutionNumber { get; }

    string? DeduplicationKey { get; }

    string? CorrelationKey { get; }

    string? ConcurrencyKey { get; }

    byte? InputFormatId { get; }

    byte[]? Input { get; }

    DateTime? NextRunAtUtc { get; }

    DateTime? CreatedAtUtc { get; }

    int? FailureCount { get; }

    Guid? JobRef { get; }

    int? TenantId { get; }

    long? LaneId { get; }

    long? ParentId { get; }
}
