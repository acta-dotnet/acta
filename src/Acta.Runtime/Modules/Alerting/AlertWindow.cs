namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Shared dedupe-window math for the alert-raising paths. The window start is the supplied UTC
/// instant floored to a multiple of the configured window - the caller's now on the manual paths
/// (in-handler and operator raises), the projected event's <c>created_at_utc</c> in the
/// <c>sys.alerts</c> projector - so repeats sharing a deduplication key inside one window collapse
/// onto the same <c>alerts</c> row, and a replayed event re-derives the bucket it landed in first.
/// </summary>
internal static class AlertWindow
{
    /// <summary>
    /// Floors <paramref name="instant"/> to the start of its <paramref name="window"/> bucket (UTC). A
    /// non-positive window returns <paramref name="instant"/> stamped UTC (every call its own bucket).
    /// </summary>
    public static DateTime FloorStart(DateTime instant, TimeSpan window)
    {
        if (window.Ticks <= 0)
        {
            return DateTime.SpecifyKind(instant, DateTimeKind.Utc);
        }
        var floored = instant.Ticks - (instant.Ticks % window.Ticks);
        return new DateTime(floored, DateTimeKind.Utc);
    }
}
