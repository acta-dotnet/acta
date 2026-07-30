using Acta.Payloads;

namespace Acta.Modules.Execution.Jobs;

/// <summary>
/// Flat job snapshot row in SELECT order; maps public refs after binding.
/// </summary>
internal sealed record JobSnapshotRow(
    long JobId,
    Guid JobRef,
    long? LineageRootId,
    Guid? LineageRootJobRef,
    long? ParentJobId,
    Guid? ParentJobRef,
    string? DeduplicationKey,
    string? CorrelationKey,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    JobPriorityCode Priority,
    int ExecutionNumber,
    short FailureCount,
    byte InputFormatId,
    DateTime? NextRunAtUtc,
    int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    string? ExclusiveKey,
    DateTime? RetentionUntilUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int? TenantId,
    string? TenantKey,
    int JobDefinitionId
)
{
    // Named, not positional: this row is in SELECT order and the snapshot in entity order, and both
    // carry runs of same-typed fields a positional call would silently swap.
    public JobSnapshot ToSnapshot() =>
        new(
            JobId: JobId,
            JobRef: new JobRef(JobRef),
            JobNamespace: JobNamespace,
            JobDefinitionId: JobDefinitionId,
            JobName: JobName,
            LineageRootId: LineageRootId,
            LineageRootJobRef: LineageRootJobRef is { } rootRef ? new JobRef(rootRef) : null,
            ParentJobId: ParentJobId,
            ParentJobRef: ParentJobRef is { } parentRef ? new JobRef(parentRef) : null,
            TenantId: TenantId,
            TenantKey: TenantKey,
            DeduplicationKey: PersistedDeduplicationKey.Normalize(DeduplicationKey),
            CorrelationKey: CorrelationKey,
            ExclusiveKey: ExclusiveKey,
            InputFormatId: InputFormatId,
            CreatedAtUtc: CreatedAtUtc,
            Status: Status,
            Priority: Priority,
            NextRunAtUtc: NextRunAtUtc,
            ExecutionNumber: ExecutionNumber,
            FailureCount: FailureCount,
            LeasedByWorkerId: LeasedByWorkerId,
            LeaseExpiresAtUtc: LeaseExpiresAtUtc,
            RetentionUntilUtc: RetentionUntilUtc,
            ModifiedAtUtc: ModifiedAtUtc
        );
}

/// <summary>
/// Header row of the explain read: the job/runtime snapshot plus the leasing worker's liveness (via a
/// LEFT JOIN on <c>runtimes.leased_by_worker_id</c>) and the latest reason recorded on the timeline
/// (the most recent <c>events</c> row carrying a reason, joined by <c>MAX(id)</c>). Columns are in
/// SELECT order; worker and reason columns are null when the Job holds no lease / has no reasoned event.
/// </summary>
internal sealed record ExplainHeaderRow(
    long JobId,
    Guid JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    int ExecutionNumber,
    short FailureCount,
    short MaxAttemptsEffective,
    DateTime? NextRunAtUtc,
    int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    string? WorkerDeploymentVersion,
    WorkerStatusCode? WorkerStatus,
    DateTime? WorkerLastSeenAtUtc,
    JobEventReasonCode? LatestReasonCode,
    string? LatestReasonMessage,
    int? LastExecutedByWorkerId,
    string? LastExecutedByWorkerName
);

/// <summary>One <c>steps</c> row for the explain read, in SELECT order.</summary>
internal sealed record ExplainStepRow(
    string Name,
    JobStepStateCode State,
    short AttemptNumber,
    DateTime? NextRetryAtUtc,
    string? ReasonMessage
);

/// <summary>One <c>checkpoints</c> row for the explain read (signal / timer / variable / progress / child-latch), in SELECT order.</summary>
internal sealed record ExplainCheckpointRow(JobCheckpointKindCode Kind, string Name, JobCheckpointStateCode? State, DateTime? DueAtUtc);

/// <summary>The three consistent result sets the explain read returns for one Job.</summary>
internal sealed record JobExplainData(
    ExplainHeaderRow Header,
    IReadOnlyList<ExplainStepRow> Steps,
    IReadOnlyList<ExplainCheckpointRow> Checkpoints
);

/// <summary>One Job node (focus or ancestor) for the lineage read, in SELECT order.</summary>
internal sealed record LineageJobRow(
    long JobId,
    Guid JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    long? ParentJobId,
    Guid? ParentJobRef,
    long? LineageRootId,
    Guid? LineageRootJobRef,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);

/// <summary>One <c>steps</c> row of the focused Job for the lineage read, in SELECT order.</summary>
internal sealed record LineageStepRow(string Name, JobStepStateCode State);

/// <summary>One direct child of the focused Job for the lineage read, in SELECT order.</summary>
internal sealed record LineageChildRow(
    long JobId,
    Guid JobRef,
    string JobName,
    JobStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
);

/// <summary>The five raw result sets the lineage read returns for one focused Job.</summary>
internal sealed record JobLineageData(
    LineageJobRow Focus,
    IReadOnlyList<LineageJobRow> Ancestors,
    IReadOnlyList<LineageStepRow> Steps,
    IReadOnlyList<ExplainCheckpointRow> Checkpoints,
    IReadOnlyList<LineageChildRow> Children
);

/// <summary>
/// One <c>results</c> row; format and data buffer reconstitute a <see cref="JobPayload"/>.
/// </summary>
internal sealed record JobResultRecord(int ExecutionNumber, JobPayloadFormat Format, ReadOnlyMemory<byte> Data, DateTime CreatedAtUtc);

/// <summary>The stored input format id and buffer for one job; None format id means no input.</summary>
internal sealed record JobInputRecord(byte FormatId, ReadOnlyMemory<byte> Data);

/// <summary>Flat input row (input_format_id, input); the input buffer is NULL for a no-input job.</summary>
internal sealed record JobInputRow(byte FormatId, byte[]? Input)
{
    public JobInputRecord ToRecord() => new(FormatId, Input ?? ReadOnlyMemory<byte>.Empty);
}

/// <summary>
/// Flat <c>checkpoints</c> row for the job read surface, in SELECT order; value bytes reconstitute a
/// <see cref="JobPayload"/> after binding (None format id / null value means no payload).
/// </summary>
internal sealed record JobCheckpointReadRow(
    JobCheckpointKindCode Kind,
    string Name,
    JobCheckpointStateCode? State,
    DateTime? DueAtUtc,
    byte ValueFormatId,
    byte[]? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public JobCheckpointItem ToItem() =>
        new(
            Kind,
            Name,
            State,
            DueAtUtc,
            ValueFormatId == 0 ? null : JobPayload.FromBytes(JobPayloadFormat.ForId(ValueFormatId), Value ?? []),
            CreatedAtUtc,
            ModifiedAtUtc
        );
}

/// <summary>
/// Flat result row; resolves the payload format registry value after binding.
/// </summary>
internal sealed record JobResultRow(int ExecutionNumber, byte FormatId, ReadOnlyMemory<byte> Data, DateTime CreatedAtUtc)
{
    public JobResultRecord ToRecord() => new(ExecutionNumber, JobPayloadFormat.ForId(FormatId), Data, CreatedAtUtc);
}

/// <summary>
/// One <c>job</c> row projected for the jobs list read; payload columns are never selected.
/// </summary>
internal sealed record JobListRow(
    long JobId,
    Guid JobRef,
    string JobNamespace,
    string JobName,
    int? TenantId,
    long? ParentJobId,
    Guid? ParentJobRef,
    long? LineageRootId,
    Guid? LineageRootJobRef,
    string? DeduplicationKey,
    string? CorrelationKey,
    JobStatusCode Status,
    JobPriorityCode Priority,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    DateTime? NextRunAtUtc,
    int ExecutionNumber,
    short FailureCount
);

/// <summary>
/// Flat jobs list row in SELECT order.
/// </summary>
internal sealed record JobListProjectionRow(
    long JobId,
    string JobNamespace,
    string JobName,
    long? ParentJobId,
    long? LineageRootId,
    string? DeduplicationKey,
    string? CorrelationKey,
    JobStatusCode Status,
    JobPriorityCode Priority,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    DateTime? NextRunAtUtc,
    int ExecutionNumber,
    short FailureCount,
    Guid JobRef,
    Guid? ParentJobRef,
    Guid? LineageRootJobRef,
    int? TenantId
)
{
    public JobListRow ToListRow() =>
        new(
            JobId,
            JobRef,
            JobNamespace,
            JobName,
            TenantId,
            ParentJobId,
            ParentJobRef,
            LineageRootId,
            LineageRootJobRef,
            PersistedDeduplicationKey.Normalize(DeduplicationKey),
            CorrelationKey,
            Status,
            Priority,
            CreatedAtUtc,
            ModifiedAtUtc,
            NextRunAtUtc,
            ExecutionNumber,
            FailureCount
        );
}

/// <summary>Normalizes keys read from storage without applying user-key reserved-prefix rules.</summary>
internal static class PersistedDeduplicationKey
{
    public static string? Normalize(string? value) => value is null ? null : IdentifierSyntax.NormalizeKeyLookup(value, nameof(value));
}

/// <summary>Row-to-contract mapper shared by the provider job stores.</summary>
internal static class JobListRowMapping
{
    public static JobListItem ToItem(this JobListRow row) =>
        new(
            row.JobId,
            new JobRef(row.JobRef),
            row.JobNamespace,
            row.JobName,
            row.TenantId,
            row.ParentJobId,
            row.ParentJobRef is { } parentRef ? new JobRef(parentRef) : null,
            row.LineageRootId,
            row.LineageRootJobRef is { } rootRef ? new JobRef(rootRef) : null,
            row.DeduplicationKey,
            row.CorrelationKey,
            row.Status,
            row.Priority,
            row.CreatedAtUtc,
            row.ModifiedAtUtc,
            row.NextRunAtUtc,
            row.ExecutionNumber,
            row.FailureCount
        );
}
