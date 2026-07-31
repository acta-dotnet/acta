namespace Acta.Runtime.Modules.Outbox;

/// <summary>
/// One claimed external-outbox row: the positional projection of a source row and the fields the relay
/// needs to reconstruct a <see cref="JobEnqueueRequest"/> and finalize under the current claim token. The
/// constructor order is the <c>[DbProjection]</c> contract: it must match the SELECT column order in every
/// provider's <c>ClaimDueRows.sql</c> at every position. Binary is read as <c>byte[]?</c> like the ledger
/// input projections. <c>PriorityCode</c> stays null when the producer set no override; the relay treats
/// null as Normal only while ordering the transport queue and leaves it null in the reconstructed request.
/// </summary>
internal sealed record OutboxRow(
    Guid OutboxId,
    string JobNamespace,
    string JobName,
    byte InputFormatId,
    byte[]? InputData,
    string DeduplicationKey,
    string? CorrelationKey,
    string? ExclusiveKey,
    byte? PriorityCode,
    DateTime? NextRunAtUtc,
    int? DelaySeconds,
    string? TenantKey,
    string? MetaJson,
    DateTime CreatedAtUtc,
    int FailureCount
);

/// <summary>
/// A bounded claim request: expired-lease recovery plus a due-Pending claim of at most
/// <paramref name="BatchSize"/> rows, stamping <paramref name="ClaimToken"/> valid for
/// <paramref name="LeaseTtlSeconds"/> from the source-database clock.
/// </summary>
internal sealed record ClaimOutboxCommand(Guid ClaimToken, int BatchSize, int LeaseTtlSeconds);

/// <summary>Token-CAS finalize of a set of claimed rows by delete or release; neither records a count or error.</summary>
internal sealed record FinalizeOutboxCommand(Guid ClaimToken, IReadOnlyList<Guid> OutboxIds);

/// <summary>Token-CAS reschedule of recoverable failures back to Pending with backoff.</summary>
internal sealed record RescheduleOutboxCommand(Guid ClaimToken, IReadOnlyList<OutboxReschedule> Rows);

/// <summary>
/// Per-row reschedule outcome: the row's own incremented failure count, the backoff duration in whole
/// seconds the source database adds to its own clock (<c>next_attempt = source_db_now + BackoffSeconds</c>,
/// so eligibility is anchored to the source clock, not the ledger clock), and the bounded error.
/// </summary>
internal readonly record struct OutboxReschedule(Guid OutboxId, int FailureCount, int BackoffSeconds, string LastError);

/// <summary>
/// Token-CAS transition of claimed rows to Quarantined, one <see cref="OutboxQuarantine"/> per row so a
/// coalesced group can partially quarantine: each row records its own consumed failure count and error.
/// </summary>
internal sealed record QuarantineOutboxCommand(Guid ClaimToken, IReadOnlyList<OutboxQuarantine> Rows);

/// <summary>Per-row quarantine outcome: the row's own consumed failure count and bounded error.</summary>
internal readonly record struct OutboxQuarantine(Guid OutboxId, int FailureCount, string? LastError);

/// <summary>
/// A deterministic, non-recoverable defect in one external-outbox row (malformed <c>meta</c>, missing
/// tag name, or a payload above the target's hard inline cap). The relay quarantines the row
/// immediately rather than accruing retries.
/// </summary>
internal sealed class OutboxContractException(string message) : Exception(message);

/// <summary>
/// Raised once at the end of a <c>sys.outbox</c> tick that quarantined one or more rows, after all
/// valid work committed. Its bounded summary (source, count, sample ids) flows through the normal
/// <c>SysCritical</c> / <c>AuditLevel.Failures</c> path, so operators get one deduplicated alert
/// rather than one per row.
/// </summary>
internal sealed class OutboxQuarantineTickException(string sourceName, IReadOnlyList<Guid> quarantinedIds)
    : Exception(BuildMessage(sourceName, quarantinedIds))
{
    public string SourceName { get; } = sourceName;

    public int QuarantinedCount { get; } = quarantinedIds.Count;

    /// <summary>The bounded 10-id sample the alert and the paired diagnostic log line share, so neither
    /// prints the full (up to 5,120) id set.</summary>
    internal static string FormatSample(IReadOnlyList<Guid> ids)
    {
        var sample = string.Join(", ", ids.Take(10));
        var suffix = ids.Count > 10 ? ", ..." : "";
        return $"{sample}{suffix}";
    }

    private static string BuildMessage(string sourceName, IReadOnlyList<Guid> ids) =>
        $"Outbox source '{sourceName}' quarantined {ids.Count} row(s): [{FormatSample(ids)}].";
}
