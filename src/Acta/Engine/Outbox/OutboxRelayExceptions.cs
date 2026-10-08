namespace Acta;

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
