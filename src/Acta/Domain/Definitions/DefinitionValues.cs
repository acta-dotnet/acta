namespace Acta;

/// <summary>
/// One descriptor materialized into the per-definition <c>definitions</c> column values. Null policy
/// overrides resolve to their framework defaults here, C#-side, so the providers cannot drift; the dialect
/// binds the row into its native batch shape (TVP on SQL Server, typed arrays on PostgreSQL) verbatim.
/// </summary>
internal sealed record DefinitionValues(
    string Name,
    byte PriorityCode,
    int MaxAttempts,
    int? ConcurrencyLimit,
    string? RateLimit,
    string? RateName,
    string? Lane,
    string Backoff,
    int ExecutionTimeoutSeconds,
    int DeadlineSeconds,
    byte DeadlineBehaviorCode,
    int JobRetentionSeconds,
    string InputTypeName,
    string? OutputTypeName,
    byte InputFormatId,
    string InputFormatName,
    byte OutputFormatId,
    string OutputFormatName,
    byte AuditLevelCode,
    byte AlertProfileCode,
    byte TenantRequirementCode,
    string? AlertChannelName,
    string? RunbookUrl,
    string? DisplayName,
    string? Description,
    string DefinitionHash
);
