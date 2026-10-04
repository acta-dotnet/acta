namespace Acta.Runtime;

/// <summary>
/// Flat database row for a stored definition; maps into the nested contract/policy shape after binding.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record GetDefinitionContractsRow(
    string Name,
    DateTime ManifestGenerationAtUtc,
    string InputTypeName,
    string? OutputTypeName,
    byte InputFormatId,
    string InputFormatName,
    byte OutputFormatId,
    string OutputFormatName,
    int Id,
    string DefinitionHash,
    DefinitionStatusCode Status,
    DateTime ModifiedAtUtc,
    JobPriorityCode Priority,
    int MaxAttempts,
    int? ConcurrencyLimit,
    string? RateLimit,
    string? RateKey,
    string Backoff,
    int ExecutionTimeoutSeconds,
    int DeadlineSeconds,
    DeadlineBehaviorCode DeadlineBehavior,
    int JobRetentionSeconds,
    DefinitionAuditLevelCode AuditLevel,
    AlertProfileCode AlertProfile,
    string? AlertChannelName,
    string? RunbookUrl
)
{
    public StoredDefinitionContract ToContract() =>
        new(
            Name,
            ManifestGenerationAtUtc,
            new DefinitionContract(InputTypeName, OutputTypeName, InputFormatId, InputFormatName, OutputFormatId, OutputFormatName),
            Id,
            DefinitionHash,
            Status,
            ModifiedAtUtc,
            new EffectiveJobPolicy(
                Priority,
                MaxAttempts,
                ConcurrencyLimit,
                RateLimit,
                RateKey,
                Backoff,
                ExecutionTimeoutSeconds,
                DeadlineSeconds,
                DeadlineBehavior,
                JobRetentionSeconds,
                AuditLevel,
                AlertProfile,
                AlertChannelName,
                RunbookUrl
            )
        );
}
