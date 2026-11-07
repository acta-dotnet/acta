namespace Acta;

/// <summary>
/// Flat database row for a stored definition; maps into the nested contract/policy shape after binding.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record ListDefinitionContractsRow(
    string DefinitionName,
    string InputTypeName,
    string? ResultTypeName,
    byte InputFormatId,
    string InputFormatName,
    byte ResultFormatId,
    string ResultFormatName,
    int DefinitionId,
    string DefinitionHash,
    DefinitionStatusCode Status,
    JobPriorityCode Priority,
    int MaxAttempts,
    int? ConcurrencyLimit,
    string? RateLimit,
    string? RateName,
    string Backoff,
    int ExecutionTimeoutSeconds,
    int DeadlineSeconds,
    DeadlineBehaviorCode DeadlineBehavior,
    int JobRetentionSeconds,
    EventRecordingCode EventRecording,
    AlertProfileCode AlertProfile,
    FiringFailurePolicyCode FiringFailurePolicy,
    ScheduledDeliveryCode ScheduledDelivery,
    string? AlertChannelName,
    string? RunbookUrl
)
{
    public StoredDefinitionContract ToContract() =>
        new(
            DefinitionName,
            new DefinitionContract(InputTypeName, ResultTypeName, InputFormatId, InputFormatName, ResultFormatId, ResultFormatName),
            DefinitionId,
            DefinitionHash,
            Status,
            new EffectiveJobPolicy(
                Priority,
                MaxAttempts,
                ConcurrencyLimit,
                RateLimit,
                RateName,
                Backoff,
                ExecutionTimeoutSeconds,
                DeadlineSeconds,
                DeadlineBehavior,
                JobRetentionSeconds,
                EventRecording,
                AlertProfile,
                FiringFailurePolicy,
                ScheduledDelivery,
                AlertChannelName,
                RunbookUrl
            )
        );
}
