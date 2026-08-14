namespace Acta;

/// <summary>
/// Schedules domain: operator pause/resume of a single named <c>JobSchedule</c> plus the keyset-paginated
/// schedule list. Reached through <see cref="IActaOperations.Schedules"/>.
/// </summary>
public interface ISchedules
{
    /// <summary>Pause the schedule identified by <paramref name="schedule"/>. Null <paramref name="untilUtc"/> pauses indefinitely; a timestamp auto-resumes. Stamps actor=Operator and records <paramref name="reasonMessage"/>. <paramref name="actorKey"/> is recorded on the audit event as the operator identity (e.g. the authenticated principal name); null when unknown. Missing schedule is NotFound; orphaned or past untilUtc is Rejected.</summary>
    ValueTask<ScheduleControlResult> PauseAsync(
        ScheduleLookup schedule,
        DateTime? untilUtc = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Resume the schedule identified by <paramref name="schedule"/>: clears the pause and reconciles the cursor by misfire policy. <paramref name="actorKey"/> is recorded on the audit event as the operator identity (e.g. the authenticated principal name); null when unknown. Missing schedule is NotFound; orphaned is Rejected.</summary>
    ValueTask<ScheduleControlResult> ResumeAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Sets a full-set operator override on <paramref name="schedule"/>'s expression and/or time zone, CAS-guarded on <paramref name="expectedVersion"/>. A null <paramref name="expression"/>/<paramref name="timeZoneId"/> clears that override (falls back to the definition default); a non-null value replaces it after validating that it parses (Cron or ISO 8601, per the schedule's existing kind) and, for a time zone, that the id resolves via <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/>; an invalid value throws <see cref="ArgumentException"/> before any write. <paramref name="actorKey"/> is recorded on the audit event as the operator identity; null when unknown. Missing/orphaned schedule is NotFound; a stale <paramref name="expectedVersion"/> is Rejected carrying the schedule's current state so the caller can re-read.</summary>
    ValueTask<ScheduleControlResult> UpdateOverridesAsync(
        ScheduleLookup schedule,
        int expectedVersion,
        string? expression,
        string? timeZoneId,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Fires the schedule identified by <paramref name="schedule"/> right now: pulls the owning slot's cursor to the current instant so the next claim sweep picks it up immediately, leaving the schedule's own cursor (and cadence) untouched. <paramref name="actorKey"/> is recorded on the audit event as the operator identity; null when unknown. <paramref name="reasonMessage"/> rides the audit event's reason message alongside the schedule name (this verb writes no schedule row, so the reasonMessage is not persisted anywhere else). Missing/orphaned schedule is NotFound; a paused schedule is Rejected (resume first); a slot already Dispatched or Executing is Rejected (a fire is already in flight).</summary>
    ValueTask<ScheduleControlResult> TriggerNowAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>List schedules next-run first, optionally filtered by namespace, name, origin, and liveness.</summary>
    ValueTask<PagedResult<ScheduleListItem>> ListAsync(ListSchedulesQuery query, CancellationToken ct = default);

    /// <summary>
    /// Previews up to <paramref name="count"/> (clamped to [1, 50]) upcoming fire instants for the
    /// schedule identified by <paramref name="schedule"/>, computed live from the current clock instant
    /// using its effective expression/time zone; no persisted cursor is read or advanced, so this is a
    /// pure "what would it do" projection, safe to poll. A paused schedule still previews: pause is an
    /// operator override of whether the schedule fires, not a property of the expression itself, so the
    /// answer to "what would it do if it were live" is unaffected. Returns fewer than <paramref name="count"/>
    /// entries (down to none) when the expression is exhausted (e.g. an unsatisfiable cron field
    /// combination). Missing schedule is null.
    /// </summary>
    ValueTask<SchedulePreview?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default);
}
