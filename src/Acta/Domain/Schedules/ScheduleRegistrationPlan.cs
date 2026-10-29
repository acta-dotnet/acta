namespace Acta;

/// <summary>
/// What a worker start writes for a definition's schedules, against what is stored: a new schedule seeds from the
/// first occurrence after now; one whose timing fingerprint still matches keeps its cursor, paused or not; one whose
/// timing changed discards the old stream's undecided occurrences and seeds from the new timing (registration moves
/// the watermark and audits the retime). A calendar binding resolves by name through <c>bind</c>, a code calendar
/// before the catalog, and a catalog binding keeps the version the stored schedule pinned; a name nothing resolves
/// fails the start, naming the schedule. The slot's status is never part of the plan: admission moves slots.
/// </summary>
internal static class ScheduleRegistrationPlan
{
    /// <summary>A process with no calendars, which every cron and interval plan can use.</summary>
    public static readonly Func<string, CalendarRoleCode, (CalendarOriginCode Origin, int Version)?> NoCalendars = static (_, _) => null;

    public static IReadOnlyList<SlotSchedule> Plan(
        IReadOnlyList<ScheduleDescriptor> declared,
        IReadOnlyDictionary<string, StoredScheduleState> storedByName,
        DateTime nowUtc,
        Func<ScheduleTiming, ScheduleSource> resolve,
        Func<string, CalendarRoleCode, (CalendarOriginCode Origin, int Version)?> bind
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
            ScheduleTiming timing;
            ScheduleSource source;
            try
            {
                (CalendarOriginCode Origin, int Version)? calendar =
                    d.ExpressionKind == ScheduleExpressionKindCode.Calendar
                        ? Bind(d.Expression, CalendarRoleCode.Occurrence, stored, bind)
                        : null;
                (CalendarOriginCode Origin, int Version)? availability = d.AvailabilityCalendarName is { } name
                    ? Bind(name, CalendarRoleCode.Availability, stored, bind)
                    : null;
                timing = new ScheduleTiming(
                    d.Expression,
                    d.ExpressionKind,
                    timeZone,
                    anchor,
                    calendar?.Origin,
                    calendar?.Version,
                    d.AvailabilityCalendarName,
                    availability?.Origin,
                    availability?.Version
                );
                source = resolve(timing).Bound();
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Schedule '{d.ScheduleName}' of job '{d.JobName}' cannot register: {ex.Message}", ex);
            }

            var fingerprint = TimingFingerprint.Compute(timing);
            var next =
                stored is not null && stored.TimingFingerprint == fingerprint ? stored.NextOccurrenceAtUtc
                : anchor is { } a ? NextOccurrenceCalculator.FirstAfter(source, a, nowUtc)
                : NextOccurrenceCalculator.Next(source, nowUtc);
            schedules.Add(
                new SlotSchedule(
                    d.ScheduleName,
                    d.Expression,
                    timeZone,
                    d.MisfireStrategy,
                    d.ExpressionKind,
                    d.Description,
                    next,
                    anchor,
                    d.MaxOutstanding,
                    d.FullBehavior,
                    d.ExpireAfterSeconds,
                    d.ExpireAction == ScheduleExpireActionCode.Unspecified ? null : d.ExpireAction,
                    timing.CalendarOrigin,
                    timing.CalendarVersion,
                    timing.AvailabilityCalendarName,
                    timing.AvailabilityOrigin,
                    timing.AvailabilityVersion
                )
            );
        }

        return schedules;
    }

    /// <summary>
    /// What a binding resolves to: a catalog binding the stored schedule already pinned keeps its version, since
    /// publication moves pins and registration never resets them; otherwise what <paramref name="bind"/> finds now.
    /// </summary>
    private static (CalendarOriginCode Origin, int Version) Bind(
        string name,
        CalendarRoleCode role,
        StoredScheduleState? stored,
        Func<string, CalendarRoleCode, (CalendarOriginCode Origin, int Version)?> bind
    )
    {
        var occurrence = role == CalendarRoleCode.Occurrence;
        var storedName = occurrence
            ? (stored?.ExpressionKind == ScheduleExpressionKindCode.Calendar ? stored.Expression : null)
            : stored?.AvailabilityCalendarName;
        var storedOrigin = occurrence ? stored?.CalendarOrigin : stored?.AvailabilityOrigin;
        var storedPin = occurrence ? stored?.CalendarVersion : stored?.AvailabilityVersion;
        var found = bind(name, role);
        if (
            found is { Origin: CalendarOriginCode.Catalog }
            && storedName == name
            && storedOrigin == CalendarOriginCode.Catalog
            && storedPin is { } kept
        )
        {
            return (CalendarOriginCode.Catalog, kept);
        }

        return found
            ?? throw new InvalidOperationException(
                $"The {(occurrence ? "calendar" : "availability calendar")} '{name}' is not declared in this process and has no published version in the namespace's calendar catalog or the shared one."
            );
    }
}
