namespace Acta.Runtime;

/// <summary>
/// Header row of the explain read: the job/runtime snapshot plus the leasing worker's liveness (via a
/// LEFT JOIN on <c>runtimes.leased_by_worker_id</c>) and the latest reason recorded on the timeline
/// (the most recent <c>events</c> row carrying a reason, joined by <c>MAX(id)</c>). Columns are in
/// SELECT order; worker and reason columns are null when the Job holds no lease / has no reasoned event.
/// </summary>
[DbProjection]
internal sealed record ExplainHeaderRow(
    long JobId,
    Guid JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    int ExecutionNumber,
    int FailureCount,
    int MaxAttemptsEffective,
    DateTime? NextRunAtUtc,
    int? LeasedByWorkerId,
    DateTime? LeaseExpiresAtUtc,
    string? WorkerDeploymentVersion,
    WorkerStatusCode? WorkerStatus,
    DateTime? WorkerLastHeartbeatAtUtc,
    EventReasonCode? LatestReasonCode,
    string? LatestReasonMessage,
    int? LastExecutedByWorkerId,
    string? LastExecutedByWorkerName,
    Guid? LeasedByWorkerRef,
    Guid? LastExecutedByWorkerRef
);

/// <summary>One <c>steps</c> row for the explain read, in SELECT order.</summary>
[DbProjection]
internal sealed record ExplainStepRow(
    string Name,
    StepStatusCode Status,
    int AttemptNumber,
    DateTime? NextRetryAtUtc,
    string? ReasonMessage
);

/// <summary>One <c>checkpoints</c> row for the explain read (signal / timer / variable / progress / child-latch), in SELECT order.</summary>
[DbProjection]
internal sealed record ExplainCheckpointRow(CheckpointKindCode Kind, string Name, CheckpointStatusCode? Status, DateTime? DueAtUtc);

/// <summary>The three consistent result sets the explain read returns for one Job.</summary>
internal sealed record JobExplanationRows(
    ExplainHeaderRow Header,
    IReadOnlyList<ExplainStepRow> Steps,
    IReadOnlyList<ExplainCheckpointRow> Checkpoints
);

/// <summary>One <c>steps</c> row of the focused Job for the lineage read, in SELECT order.</summary>
[DbProjection]
internal sealed record LineageStepRow(string Name, StepStatusCode Status);

/// <summary>The five result sets the lineage read returns for one focused Job; the job nodes bind the public models.</summary>
internal sealed record JobLineageRows(
    JobLineageJob Focus,
    IReadOnlyList<JobLineageJob> Ancestors,
    IReadOnlyList<LineageStepRow> Steps,
    IReadOnlyList<ExplainCheckpointRow> Checkpoints,
    IReadOnlyList<JobLineageChild> Children
);

/// <summary>Flat input row (input_format_id, input); the input buffer is NULL for a no-input job.</summary>
[DbProjection]
internal sealed record JobInputRow(byte FormatId, byte[]? Input);

/// <summary>
/// Flat <c>checkpoints</c> row for the job read surface, in SELECT order; value bytes reconstitute a
/// <see cref="JobPayload"/> after binding (None format id / null value means no payload).
/// </summary>
[DbProjection]
internal sealed record CheckpointReadRow(
    CheckpointKindCode Kind,
    string Name,
    CheckpointStatusCode? Status,
    DateTime? DueAtUtc,
    byte ValueFormatId,
    byte[]? Value,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public CheckpointItem ToItem() =>
        new(
            Kind,
            Name,
            Status,
            DueAtUtc,
            ValueFormatId == 0 ? null : JobPayload.FromBytes(JobPayloadFormat.ForId(ValueFormatId), Value ?? []),
            CreatedAtUtc,
            ModifiedAtUtc
        );
}

/// <summary>One <c>results</c> row; its format and data buffer reconstitute a <see cref="JobPayload"/>.</summary>
[DbProjection]
internal sealed record JobResultRow(int ExecutionNumber, byte FormatId, ReadOnlyMemory<byte> Data, DateTime CreatedAtUtc)
{
    public JobPayloadFormat Format => JobPayloadFormat.ForId(FormatId);
}

/// <summary>Normalizes keys read from storage without applying user-key reserved-prefix rules.</summary>
internal static class PersistedDeduplicationKey
{
    public static string? Normalize(string? value) => value is null ? null : IdentifierSyntax.NormalizeKeyLookup(value, nameof(value));
}
