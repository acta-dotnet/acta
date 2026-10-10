namespace Acta;

/// <summary>
/// <see cref="ISchedules"/> plus the recurring-slot writes and reads that worker startup and each recurring fire run.
/// </summary>
internal interface ISchedulesOperations : ISchedules
{
    /// <summary>
    /// Registers every definition's recurring slot job and its schedules in one set-based call, answering each slot.
    /// Every host re-registers every slot at startup, so this runs against slots another host may be executing: it
    /// re-asserts the declaration on an idle slot, overwriting an operator reprioritize, and leaves a Dispatched,
    /// Executing, held, or finished slot as it stands.
    /// </summary>
    Task<RegisterSchedulesResponse> RegisterSchedulesAsync(RegisterSchedulesCommand command, CancellationToken ct);

    /// <summary>
    /// The namespace's persisted (non-orphaned) per-schedule cursors. Drives startup misfire-aware reconciliation and the
    /// slot-pause decision for definitions that dropped every schedule.
    /// </summary>
    Task<GetScheduleStateResponse> GetScheduleStateAsync(int namespaceId, CancellationToken ct);

    /// <summary>
    /// The effective (non-orphaned) schedules of one recurring slot, expression and time zone resolved to
    /// override-or-original. Read once per recurring fire for the due set and the recompute.
    /// </summary>
    Task<GetLiveSchedulesResponse> GetLiveSchedulesAsync(long jobId, CancellationToken ct);
}
