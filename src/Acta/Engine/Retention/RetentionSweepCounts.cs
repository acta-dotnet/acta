namespace Acta;

/// <summary>
/// Per-section deleted counts from one <c>purge_expired_data</c> sweep.
/// <see cref="UndeliveredAlertsPurged"/> is counted apart from <see cref="Alerts"/> because an alert
/// aged out before delivery settled is a lost operator signal, not routine housekeeping.
/// </summary>
internal readonly record struct RetentionSweepCounts(
    int Jobs,
    int Events,
    int Alerts,
    int UndeliveredAlertsPurged,
    int Workers,
    int Locks,
    int Lanes
);
