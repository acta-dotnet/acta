namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Shared dedupe-window math for the alert-raising paths (in-handler, operator, and the <c>sys.alerts</c>
/// projector). The window start is the caller's UTC instant floored to a multiple of the configured
/// window, so repeats sharing a deduplication key inside one window collapse onto the same <c>alerts</c> row.
/// </summary>
internal static class AlertWindow
{
    /// <summary>
    /// Floors <paramref name="now"/> to the start of its <paramref name="window"/> bucket (UTC). A
    /// non-positive window returns <paramref name="now"/> stamped UTC (every call its own bucket).
    /// </summary>
    public static DateTime FloorStart(DateTime now, TimeSpan window)
    {
        if (window.Ticks <= 0)
        {
            return DateTime.SpecifyKind(now, DateTimeKind.Utc);
        }
        var floored = now.Ticks - (now.Ticks % window.Ticks);
        return new DateTime(floored, DateTimeKind.Utc);
    }
}
