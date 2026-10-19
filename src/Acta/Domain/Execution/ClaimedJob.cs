namespace Acta;

/// <summary>
/// One claimed row: the full job row needed to dispatch, and for the runner to take the <c>concurrency_key</c> slot
/// before the handler, without re-reading <c>jobs</c>; tags and schedules load separately on demand, never joined into
/// the hot claim. <c>FailureCount</c> feeds the failure-budget decision computed in C# at completion; a completion
/// matches on worker and <c>ExecutionNumber</c>, which only a claim moves. <c>LaneId</c> and <c>ParentId</c> never change after insert, so the completion trusts them as the claim read them.
/// </summary>
internal sealed record ClaimedJob(
    long JobId,
    Guid JobRef,
    int DefinitionId,
    int? TenantId,
    int ExecutionNumber,
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ConcurrencyKey,
    byte InputFormatId,
    ReadOnlyMemory<byte> Input,
    DateTime? NextRunAtUtc,
    DateTime CreatedAtUtc,
    int FailureCount,
    long? LaneId = null,
    long? ParentId = null
)
{
    /// <summary>Reads a job row of either claim routine; the horizon sentinel row is the claim scan's own.</summary>
    public static ClaimedJob From(IClaimColumns row) =>
        new(
            JobId: row.JobId!.Value,
            JobRef: Required(row.JobRef, "job_ref"),
            DefinitionId: Required(row.DefinitionId, "definition_id"),
            TenantId: row.TenantId,
            ExecutionNumber: Required(row.ExecutionNumber, "execution_number"),
            DeduplicationKey: row.DeduplicationKey is null
                ? null
                : KeySyntax.Canonicalize(row.DeduplicationKey, nameof(row.DeduplicationKey)),
            CorrelationKey: row.CorrelationKey,
            ConcurrencyKey: row.ConcurrencyKey,
            InputFormatId: Required(row.InputFormatId, "input_format_id"),
            Input: row.Input ?? ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: row.NextRunAtUtc,
            CreatedAtUtc: Required(row.CreatedAtUtc, "created_at_utc"),
            FailureCount: Required(row.FailureCount, "failure_count"),
            LaneId: row.LaneId,
            ParentId: row.ParentId
        );

    private static T Required<T>(T? value, string column)
        where T : struct => value ?? throw new InvalidOperationException($"claim returned a job row with NULL {column}.");
}
