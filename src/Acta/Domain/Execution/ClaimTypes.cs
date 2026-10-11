namespace Acta;

/// <summary>
/// What one claim on the hot priority-ordered scan asks for; deterministic by-id claiming lives on the separate
/// <c>claim_job</c> path (testing and debug), so the production claim query carries no explicit-id branch.
/// <c>ExcludedDefinitionIds</c> holds the <c>definitions.id</c> values this worker has bounced for want of a handler:
/// null or empty applies no filter and keeps the candidate step on the pure claim-index scan, and both the candidate
/// scan and the empty-claim horizon skip an excluded row, so a worker whose only due work is excluded sleeps instead
/// of spinning at the anti-spin floor.
/// </summary>
internal sealed record ClaimOptions(
    int NamespaceId,
    int WorkerId,
    int MaxBatch,
    bool StartExecuting = false,
    int[]? ExcludedDefinitionIds = null
);

/// <summary>
/// One claimed row: the full job row needed to dispatch, and for the runner to take the <c>concurrency_key</c> slot
/// before the handler, without re-reading <c>jobs</c>; tags and schedules load separately on demand, never joined into
/// the hot claim. <c>FailureCount</c> feeds the failure-budget decision computed in C# at completion. <c>Version</c>
/// is <c>runtimes.version</c> as of this claim (the claim bumps it), threaded into <c>start_execution</c> as the CAS
/// guard: a reclaim or steal between claim and start bumps it, so a stale claim fails to start instead of double-executing.
/// <c>LaneId</c> and <c>ParentId</c> never change after insert, so the completion trusts them as the claim read them.
/// </summary>
internal sealed record ClaimedJob(
    long JobId,
    Guid JobRef,
    int NamespaceId,
    int DefinitionId,
    int? TenantId,
    int ExecutionNumber,
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ConcurrencyKey,
    byte InputFormatId,
    ReadOnlyMemory<byte> Input,
    DateTime? NextRunAtUtc,
    DateTime LeaseExpiresAtUtc,
    DateTime CreatedAtUtc,
    int FailureCount,
    int Version,
    long? LaneId = null,
    long? ParentId = null
)
{
    /// <summary>Reads a job row of either claim routine; the horizon sentinel row is the claim scan's own.</summary>
    public static ClaimedJob From(IClaimColumns row) =>
        new(
            JobId: row.JobId!.Value,
            JobRef: Required(row.JobRef, "job_ref"),
            NamespaceId: Required(row.NamespaceId, "namespace_id"),
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
            LeaseExpiresAtUtc: Required(row.LeaseExpiresAtUtc, "lease_expires_at_utc"),
            CreatedAtUtc: Required(row.CreatedAtUtc, "created_at_utc"),
            FailureCount: Required(row.FailureCount, "failure_count"),
            Version: Required(row.Version, "version"),
            LaneId: row.LaneId,
            ParentId: row.ParentId
        );

    private static T Required<T>(T? value, string column)
        where T : struct => value ?? throw new InvalidOperationException($"claim returned a job row with NULL {column}.");
}

/// <summary>
/// The empty-claim horizon: the routine's clock reading and the earliest claimable row's effective run time in the
/// namespace, <c>MIN(COALESCE(next_run_at_utc, db_now))</c> over every Ready row and every Suspended row carrying a
/// durable wait's expiration, due-now rows included (a concurrency-key bounce re-arms Ready with a forward-dated
/// <c>next_run_at_utc</c>, so it appears at its due instant). <c>NextReadyAtUtc</c> is null only when no such row
/// exists; a value at or before <c>DbNowUtc</c> means due rows were transiently locked away (SKIP LOCKED), so the
/// caller retries after a short floor rather than sleeping out the safety interval. Both instants are DB-sourced.
/// </summary>
internal readonly record struct ClaimHorizon(DateTime DbNowUtc, DateTime? NextReadyAtUtc);

/// <summary>
/// The columns both claim routines return, in SELECT order, so <see cref="ClaimedJob.From"/> reads a
/// <c>ClaimJobs</c> or a <c>ClaimJob</c> row. <see cref="JobId"/> is null only on the empty-claim horizon sentinel row.
/// </summary>
internal interface IClaimColumns
{
    long? JobId { get; }

    int? NamespaceId { get; }

    int? DefinitionId { get; }

    int? ExecutionNumber { get; }

    string? DeduplicationKey { get; }

    string? CorrelationKey { get; }

    string? ConcurrencyKey { get; }

    byte? InputFormatId { get; }

    byte[]? Input { get; }

    DateTime? NextRunAtUtc { get; }

    DateTime? LeaseExpiresAtUtc { get; }

    DateTime? CreatedAtUtc { get; }

    int? FailureCount { get; }

    int? Version { get; }

    Guid? JobRef { get; }

    int? TenantId { get; }

    DateTime? DbNowUtc { get; }

    DateTime? NextReadyAtUtc { get; }

    long? LaneId { get; }

    long? ParentId { get; }
}
