namespace Acta.Runtime.Modules.Execution.Definitions;

/// <summary>
/// A definition's effective (override-or-default) policy, read straight off the DB-computed
/// <c>*_effective</c> generated columns. The worker overlays this onto its in-memory
/// <see cref="JobDescriptor"/> index so the execution hot path (which reads the descriptor, not the DB)
/// honors operator overrides.
/// </summary>
internal sealed record EffectiveJobPolicy(
    JobPriorityCode Priority,
    short MaxAttempts,
    string Backoff,
    int ExecutionTimeoutSeconds,
    int DeadlineSeconds,
    DeadlineBehaviorCode DeadlineBehavior,
    int JobRetentionSeconds,
    JobAuditLevelCode AuditLevel,
    AlertProfileCode AlertProfile,
    string? AlertChannelName,
    string? RunbookUrl
);

/// <summary>
/// One stored definition's identity, generation, change-detection hash, status, contract columns, last
/// modification time, and effective policy. Read at startup to drive the payload-contract drift check
/// (Warn/Fail) and the registration write gate (skip the upsert when nothing is new/changed/retired), and
/// after registration (plus on the reload tick) to overlay effective policy onto the worker's descriptors.
/// </summary>
internal sealed record StoredDefinitionContract(
    string Name,
    DateTime ManifestGenerationAtUtc,
    DefinitionContract Contract,
    int Id,
    string DefinitionHash,
    JobDefinitionStatusCode Status,
    DateTime ModifiedAtUtc,
    EffectiveJobPolicy Effective
);

/// <summary>
/// Flat database row for a stored definition; maps into the nested contract/policy shape after binding.
/// </summary>
internal sealed record StoredDefinitionContractRow(
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
    JobDefinitionStatusCode Status,
    DateTime ModifiedAtUtc,
    JobPriorityCode Priority,
    short MaxAttempts,
    string Backoff,
    int ExecutionTimeoutSeconds,
    int DeadlineSeconds,
    DeadlineBehaviorCode DeadlineBehavior,
    int JobRetentionSeconds,
    JobAuditLevelCode AuditLevel,
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

/// <summary>
/// The full <c>definitions</c> row for the dashboard detail editor: identity + control, contract +
/// formats, every policy triple (code <c>default</c>, operator <c>override</c> [null = inherit],
/// DB-computed <c>effective</c>), and audit bookkeeping. The grid-shaped subset lives on
/// <c>JobDefinitionListRow</c>; this row is read one at a time by id.
/// </summary>
internal sealed record JobDefinitionDetailRow(
    int DefinitionId,
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

/// <summary>
/// One <c>definitions</c> row trimmed to what the dashboard definitions grid shows: identity,
/// status, contract type names, and the two policy fields surfaced as columns (priority and max
/// attempts, each as effective + override so the grid can flag an operator override). The full row -
/// every policy triple, formats, audit bookkeeping - is read on demand by <c>GetDefinitionAsync</c>.
/// </summary>
internal sealed record JobDefinitionListRow(
    int DefinitionId,
    string JobNamespace,
    string JobName,
    JobDefinitionStatusCode Status,
    string InputTypeName,
    string? OutputTypeName,
    JobPriorityCode? PriorityOverride,
    JobPriorityCode PriorityEffective,
    short? MaxAttemptsOverride,
    short MaxAttemptsEffective,
    DateTime ModifiedAtUtc,
    int Version
);

/// <summary>
/// One descriptor materialized into the per-definition <c>definitions</c> column values.
/// </summary>
/// <remarks>
/// Null policy overrides are resolved to their framework defaults here (C#-side) so MSSQL and PG
/// cannot drift; the dialect binds the resolved row into the provider-native batch shape (TVP on SQL
/// Server, typed arrays on Postgres) verbatim.
/// </remarks>
internal sealed record JobDefinitionRow(
    string Name,
    byte PriorityCode,
    short MaxAttempts,
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

/// <summary>
/// The contract-identifying columns of a definition, normalized the same way for the stored row and
/// an incoming descriptor so an unchanged restart never reads as drift.
/// </summary>
internal readonly record struct DefinitionContract(
    string InputTypeName,
    string? OutputTypeName,
    byte InputFormatId,
    string InputFormatName,
    byte OutputFormatId,
    string OutputFormatName
);

/// <summary>
/// Name/id row returned by <c>register_job_definitions</c>.
/// </summary>
internal readonly record struct RegisteredJobDefinition(string Name, int Id);

/// <summary>
/// Outcome of a <c>SetDefinitionOverridesAsync</c> write: which action the routine took.
/// </summary>
internal enum DefinitionOverrideAction : byte
{
    Applied = 1,
    NotFound = 2,
    VersionConflict = 3,
}

internal readonly record struct DefinitionOverrideOutcome(DefinitionOverrideAction Action);

/// <summary>Row-to-contract mappers shared by the provider definition stores.</summary>
internal static class DefinitionRowMapping
{
    public static JobDefinitionListItem ToItem(this JobDefinitionListRow row) =>
        new(
            row.DefinitionId,
            row.JobNamespace,
            row.JobName,
            row.Status,
            row.InputTypeName,
            row.OutputTypeName,
            row.PriorityOverride,
            row.PriorityEffective,
            row.MaxAttemptsOverride,
            row.MaxAttemptsEffective,
            row.ModifiedAtUtc,
            row.Version
        );

    public static JobDefinitionDetail ToDetail(this JobDefinitionDetailRow row) =>
        new(
            row.DefinitionId,
            row.JobNamespace,
            row.JobName,
            row.Status,
            row.DefinitionHash,
            row.ManifestGenerationAtUtc,
            row.InputTypeName,
            row.InputFormatId,
            row.InputFormatName,
            row.OutputTypeName,
            row.OutputFormatId,
            row.OutputFormatName,
            row.Priority,
            row.PriorityOverride,
            row.PriorityEffective,
            row.MaxAttempts,
            row.MaxAttemptsOverride,
            row.MaxAttemptsEffective,
            row.Backoff,
            row.BackoffOverride,
            row.BackoffEffective,
            row.ExecutionTimeoutSeconds,
            row.ExecutionTimeoutSecondsOverride,
            row.ExecutionTimeoutSecondsEffective,
            row.DeadlineSeconds,
            row.DeadlineSecondsOverride,
            row.DeadlineSecondsEffective,
            row.DeadlineBehavior,
            row.DeadlineBehaviorOverride,
            row.DeadlineBehaviorEffective,
            row.JobRetentionSeconds,
            row.JobRetentionSecondsOverride,
            row.JobRetentionSecondsEffective,
            row.AuditLevel,
            row.AuditLevelOverride,
            row.AuditLevelEffective,
            row.AlertProfile,
            row.AlertProfileOverride,
            row.AlertProfileEffective,
            row.AlertChannelName,
            row.AlertChannelNameOverride,
            row.AlertChannelNameEffective,
            row.RunbookUrl,
            row.RunbookUrlOverride,
            row.RunbookUrlEffective,
            row.DisplayName,
            row.DisplayNameOverride,
            row.DisplayNameEffective,
            row.Description,
            row.DescriptionOverride,
            row.DescriptionEffective,
            row.CreatedAtUtc,
            row.ModifiedAtUtc,
            row.Version
        );
}
