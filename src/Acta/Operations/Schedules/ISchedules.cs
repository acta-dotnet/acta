namespace Acta;

/// <summary>
/// Schedules domain: operator pause/resume of a single named <c>JobSchedule</c> plus the keyset-paginated
/// schedule list. Reached through <see cref="IActaOperations.Schedules"/>.
/// </summary>
public interface ISchedules
{
    /// <summary>Pause the schedule identified by <paramref name="schedule"/>. Null <paramref name="untilUtc"/> pauses indefinitely; a timestamp auto-resumes. Stamps actor=Operator and records <paramref name="reasonMessage"/>. <paramref name="actor"/> is recorded on the audit event as the operator identity (e.g. the authenticated principal name); null when unknown. A missing or orphaned schedule is NotFound; a past untilUtc is Rejected.</summary>
    ValueTask<PauseScheduleResponse> PauseAsync(
        ScheduleLookup schedule,
        DateTime? untilUtc = null,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>Resume the schedule identified by <paramref name="schedule"/>: clears the pause and reconciles the cursor by misfire policy. <paramref name="actor"/> is recorded on the audit event as the operator identity (e.g. the authenticated principal name); null when unknown. A missing or orphaned schedule is NotFound.</summary>
    ValueTask<ResumeScheduleResponse> ResumeAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>Fires the schedule identified by <paramref name="schedule"/> right now: pulls the owning slot's cursor to the current instant so the next claim sweep picks it up immediately, leaving the schedule's own cursor (and cadence) untouched. <paramref name="actor"/> is recorded on the audit event as the operator identity; null when unknown. <paramref name="reasonMessage"/> rides the audit event's reason message alongside the schedule name (this verb writes no schedule row, so the reasonMessage is not persisted anywhere else). Missing/orphaned schedule is NotFound; a paused schedule is Rejected (resume first); a slot already Executing is Rejected (a fire is already in flight).</summary>
    ValueTask<TriggerScheduleNowResponse> TriggerNowAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>List schedules next-run first, optionally filtered by namespace, name, origin, and liveness.</summary>
    ValueTask<ListSchedulesResponse> ListAsync(ListSchedulesQuery query, CancellationToken ct = default);

    /// <summary>
    /// Previews up to <paramref name="count"/> (clamped to [1, 50]) upcoming fire instants for the schedule identified
    /// by <paramref name="schedule"/>, computed live from the current clock with its expression and time
    /// zone; no persisted cursor is read or advanced, so this is a pure "what would it do" projection, safe to poll. A
    /// paused schedule still previews: pause decides whether it fires, not what the expression says. Returns fewer
    /// entries, down to none, when the expression is exhausted (an unsatisfiable cron field
    /// combination); a missing schedule is null.
    /// </summary>
    ValueTask<PreviewScheduleResponse?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default);
}
