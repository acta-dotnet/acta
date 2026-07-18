namespace Acta.Features.Execution;

/// <summary>
/// Claim request for the hot priority-ordered scan. Deterministic by-id claiming lives on the
/// separate <c>claim_one</c> path (testing/debug only), so the production claim query carries no
/// explicit-id branches.
/// </summary>
internal sealed record ClaimRequest(short NamespaceId, int WorkerId, int MaxBatch, bool StartExecuting = false);

/// <summary>
/// One claimed row: the full job row needed to dispatch (and for the runner to acquire the
/// <c>exclusive_key</c> lock before the handler) without re-reading <c>jobs</c>. Cross-table
/// extras (tags, schedules) are loaded separately on demand, never joined into the hot claim.
/// <c>FailureCount</c> feeds the failure-budget decision (re-arm vs. fail) computed in C# at
/// completion. <c>Version</c> is the <c>runtimes.version</c> token as of this claim (claim bumps it),
/// threaded into <c>start_execution</c> as the CAS guard: any reclaim or steal between claim and
/// start bumps the version, so a stale buffered claim fails to start instead of double-executing.
/// </summary>
internal sealed record ClaimedJob(
    long JobId,
    Guid JobRef,
    short NamespaceId,
    int DefinitionId,
    int? TenantId,
    int ExecutionNumber,
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ExclusiveKey,
    byte InputFormatId,
    ReadOnlyMemory<byte> Input,
    DateTime? NextRunAtUtc,
    DateTime LeaseExpiresAtUtc,
    DateTime CreatedAtUtc,
    short FailureCount,
    int Version
);

/// <summary>
/// The empty-claim horizon: the routine's clock reading and the earliest Ready row's effective run
/// time in the namespace, <c>MIN(COALESCE(next_run_at_utc, db_now))</c> over all Ready rows, due-now
/// rows included (an exclusive-key bounce re-arms Ready with a forward-dated <c>next_run_at_utc</c>,
/// so it appears here at its due instant). <c>NextReadyAtUtc</c> is null only when no Ready row
/// exists; a value at or before <c>DbNowUtc</c> means due rows exist but were transiently locked
/// away (SKIP-LOCKED), so the caller should retry after a short floor rather than sleep out the safety
/// interval. Both instants are DB-sourced, so their difference is a valid sleep duration with no
/// host-clock assumption.
/// </summary>
internal readonly record struct ClaimHorizon(DateTime DbNowUtc, DateTime? NextReadyAtUtc);

/// <summary>
/// Claim outcome: the claimed rows, plus the horizon when nothing was claimed. Exactly one of the
/// two carries information: a non-empty <c>Jobs</c> has a null <c>Horizon</c>, and an empty claim
/// has it set (the routine emits one sentinel row in place of job rows).
/// </summary>
internal sealed record ClaimResult(IReadOnlyList<ClaimedJob> Jobs, ClaimHorizon? Horizon)
{
    public static readonly ClaimResult Empty = new([], null);
}

/// <summary>
/// Flat claim routine row. <see cref="JobId"/> is null only on the empty-claim horizon sentinel row.
/// </summary>
internal sealed record ClaimReadyRow(
    long? JobId,
    short? NamespaceId,
    int? DefinitionId,
    int? ExecutionNumber,
    string? DeduplicationKey,
    string? CorrelationKey,
    string? ExclusiveKey,
    byte? InputFormatId,
    byte[]? Input,
    DateTime? NextRunAtUtc,
    DateTime? LeaseExpiresAtUtc,
    DateTime? CreatedAtUtc,
    short? FailureCount,
    int? Version,
    Guid? JobRef,
    int? TenantId,
    DateTime? DbNowUtc,
    DateTime? NextReadyAtUtc
);

/// <summary>
/// Provider-independent projection of the flat claim rows into a <see cref="ClaimResult"/>: job rows
/// map to <see cref="ClaimedJob"/>, the sentinel (null <c>JobId</c>) row becomes the horizon.
/// </summary>
internal static class ClaimResultMapper
{
    public static ClaimResult Map(IReadOnlyList<ClaimReadyRow> rows)
    {
        if (rows.Count == 0)
        {
            return ClaimResult.Empty;
        }

        ClaimHorizon? horizon = null;
        var jobs = new List<ClaimedJob>(rows.Count);
        foreach (var row in rows)
        {
            if (row.JobId is null)
            {
                horizon = new ClaimHorizon(Required(row.DbNowUtc, "db_now"), row.NextReadyAtUtc);
            }
            else
            {
                jobs.Add(ToClaimedJob(row));
            }
        }

        return new ClaimResult(jobs, horizon);
    }

    public static ClaimedJob ToClaimedJob(ClaimReadyRow row) =>
        new(
            JobId: row.JobId!.Value,
            JobRef: Required(row.JobRef, "job_ref"),
            NamespaceId: Required(row.NamespaceId, "namespace_id"),
            DefinitionId: Required(row.DefinitionId, "definition_id"),
            TenantId: row.TenantId,
            ExecutionNumber: Required(row.ExecutionNumber, "execution_number"),
            DeduplicationKey: row.DeduplicationKey is null
                ? null
                : IdentifierSyntax.NormalizeKeyLookup(row.DeduplicationKey, nameof(row.DeduplicationKey)),
            CorrelationKey: row.CorrelationKey,
            ExclusiveKey: row.ExclusiveKey,
            InputFormatId: Required(row.InputFormatId, "input_format_id"),
            Input: row.Input ?? ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: row.NextRunAtUtc,
            LeaseExpiresAtUtc: Required(row.LeaseExpiresAtUtc, "lease_expires_at_utc"),
            CreatedAtUtc: Required(row.CreatedAtUtc, "created_at_utc"),
            FailureCount: Required(row.FailureCount, "failure_count"),
            Version: Required(row.Version, "version")
        );

    private static T Required<T>(T? value, string column)
        where T : struct => value ?? throw new InvalidOperationException($"claim returned a job row with NULL {column}.");
}
