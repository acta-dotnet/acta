namespace Acta;

/// <summary>
/// What a worker start writes for a definition's schedules, against what is stored: a new schedule seeds from the
/// first occurrence after now; one whose timing fingerprint still matches keeps its cursor, paused or not; one whose
/// timing changed discards the old stream's undecided occurrences and seeds from the new timing (registration moves
/// the watermark and audits the retime). The slot's status is never part of the plan: admission moves slots.
/// </summary>
internal static class ScheduleRegistrationPlan
{
    public static IReadOnlyList<SlotSchedule> Plan(
        IReadOnlyList<ScheduleDescriptor> declared,
        IReadOnlyDictionary<string, StoredScheduleState> storedByName,
        DateTime nowUtc
    )
    {
        var schedules = new List<SlotSchedule>(declared.Count);
        foreach (var d in declared)
        {
            var timeZone = string.IsNullOrWhiteSpace(d.TimeZoneId) ? "UTC" : d.TimeZoneId;
            var stored = storedByName.GetValueOrDefault(d.ScheduleName);
            // An interval keeps the anchor its first registration set, so a redeploy never shifts its phase; a schedule
            // that becomes an interval steps from this start.
            DateTime? anchor =
                d.ExpressionKind != ScheduleExpressionKindCode.Interval ? null
                : stored?.ExpressionKind == ScheduleExpressionKindCode.Interval ? stored.IntervalAnchorAtUtc ?? nowUtc
                : nowUtc;
            var fingerprint = TimingFingerprint.Compute(new ScheduleTiming(d.Expression, d.ExpressionKind, timeZone, anchor));
            var next =
                stored is not null && stored.TimingFingerprint == fingerprint ? stored.NextOccurrenceAtUtc
                : anchor is { } a ? NextOccurrenceCalculator.FirstAfter(d.Expression, timeZone, d.ExpressionKind, a, nowUtc)
                : NextOccurrenceCalculator.Next(d.Expression, timeZone, d.ExpressionKind, nowUtc);
            schedules.Add(
                new SlotSchedule(d.ScheduleName, d.Expression, timeZone, d.MisfireStrategy, d.ExpressionKind, d.Description, next, anchor)
            );
        }

        return schedules;
    }
}
