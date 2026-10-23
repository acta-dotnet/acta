namespace Acta;

/// <summary>
/// Raised once at the end of a <c>sys.outbox</c> tick that quarantined one or more rows, after all
/// valid work committed. Its bounded summary (source, count, sample ids) flows through the normal
/// <c>SysCritical</c> / <c>EventRecording = Failures</c> path, so operators get one deduplicated alert
/// rather than one per row.
/// </summary>
internal sealed class OutboxQuarantineTickException(string sourceName, IReadOnlyList<Guid> quarantinedIds)
    : Exception(BuildMessage(sourceName, quarantinedIds))
{
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
