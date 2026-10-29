namespace Acta;

/// <summary>
/// One schedule that admitted the running firing: the nominal occurrence it selected, the zone that occurrence was
/// resolved in, and, under a coalescing misfire policy, the last due occurrence this one firing stands for
/// (<see cref="CoveredThroughUtc"/> equals <see cref="OccurrenceAtUtc"/> when it stands for itself alone). Frozen at
/// admission: every retry, reclaim, and resumed wait of the firing sees the same list.
/// </summary>
public readonly record struct TriggeringSchedule(
    string ScheduleName,
    DateTime OccurrenceAtUtc,
    string TimeZoneId,
    DateTime CoveredThroughUtc
);
