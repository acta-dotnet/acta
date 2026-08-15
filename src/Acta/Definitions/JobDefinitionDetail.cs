using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The full job definition row returned by <see cref="IDefinitions.GetAsync"/>: the whole
/// <c>definitions</c> row in its authoritative column order - identity + control, contract + formats,
/// every policy triple (code <c>default</c>, operator <c>override</c> [null = inherit], DB-computed
/// <c>effective</c>), and audit bookkeeping. Backs the dashboard's definition detail editor, where
/// operators see and edit defaults vs overrides vs effective. The grid uses the slimmer
/// <see cref="JobDefinitionListItem"/>. A definition is addressed by its natural key (namespace +
/// name); the catalog id stays off the wire.
/// </summary>
public sealed record JobDefinitionDetail(
    [property: JsonIgnore] int DefinitionId,
    string JobNamespace,
    string JobName,
    JobDefinitionStatusCode Status,
    string DefinitionHash,
    DateTime ManifestGenerationAtUtc,
    string InputTypeName,
    byte InputFormatId,
    string InputFormatName,
    string? OutputTypeName,
    byte OutputFormatId,
    string OutputFormatName,
    JobPriorityCode Priority,
    JobPriorityCode? PriorityOverride,
    JobPriorityCode PriorityEffective,
    short MaxAttempts,
    short? MaxAttemptsOverride,
    short MaxAttemptsEffective,
    string Backoff,
    string? BackoffOverride,
    string BackoffEffective,
    int ExecutionTimeoutSeconds,
    int? ExecutionTimeoutSecondsOverride,
    int ExecutionTimeoutSecondsEffective,
    int DeadlineSeconds,
    int? DeadlineSecondsOverride,
    int DeadlineSecondsEffective,
    DeadlineBehaviorCode DeadlineBehavior,
    DeadlineBehaviorCode? DeadlineBehaviorOverride,
    DeadlineBehaviorCode DeadlineBehaviorEffective,
    int JobRetentionSeconds,
    int? JobRetentionSecondsOverride,
    int JobRetentionSecondsEffective,
    JobAuditLevelCode AuditLevel,
    JobAuditLevelCode? AuditLevelOverride,
    JobAuditLevelCode AuditLevelEffective,
    AlertProfileCode AlertProfile,
    AlertProfileCode? AlertProfileOverride,
    AlertProfileCode AlertProfileEffective,
    string? AlertChannelName,
    string? AlertChannelNameOverride,
    string? AlertChannelNameEffective,
    string? RunbookUrl,
    string? RunbookUrlOverride,
    string? RunbookUrlEffective,
    string? DisplayName,
    string? DisplayNameOverride,
    string? DisplayNameEffective,
    string? Description,
    string? DescriptionOverride,
    string? DescriptionEffective,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
