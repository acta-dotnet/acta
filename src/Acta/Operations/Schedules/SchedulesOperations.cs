namespace Acta;

/// <summary>The Schedules operations: <see cref="ISchedules"/> for applications, plus the engine's calls.</summary>
internal sealed class SchedulesOperations(
    PauseScheduleHandler pauseSchedule,
    ResumeScheduleHandler resumeSchedule,
    SetScheduleOverridesHandler setScheduleOverrides,
    TriggerScheduleNowHandler triggerScheduleNow,
    ListSchedulesHandler listSchedules,
    PreviewScheduleHandler previewSchedule,
    RegisterSchedulesHandler registerSchedules,
    GetScheduleStateHandler getScheduleState,
    GetLiveSchedulesHandler getLiveSchedules
) : ISchedules
{
    public ValueTask<PauseScheduleResponse> PauseAsync(
        ScheduleLookup schedule,
        DateTime? untilUtc = null,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => pauseSchedule.HandleAsync(new PauseScheduleCommand(schedule, untilUtc, reasonMessage, actor), ct);

    public ValueTask<ResumeScheduleResponse> ResumeAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => resumeSchedule.HandleAsync(new ResumeScheduleCommand(schedule, reasonMessage, actor), ct);

    public ValueTask<SetScheduleOverridesResponse> UpdateOverridesAsync(
        ScheduleLookup schedule,
        int expectedVersion,
        string? expression,
        string? timeZoneId,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) =>
        setScheduleOverrides.HandleAsync(
            new SetScheduleOverridesCommand(schedule, expectedVersion, expression, timeZoneId, reasonMessage, actor),
            ct
        );

    public ValueTask<TriggerScheduleNowResponse> TriggerNowAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => triggerScheduleNow.HandleAsync(new TriggerScheduleNowCommand(schedule, reasonMessage, actor), ct);

    public ValueTask<ListSchedulesResponse> ListAsync(ListSchedulesQuery query, CancellationToken ct = default) =>
        listSchedules.HandleAsync(query, ct);

    public ValueTask<PreviewScheduleResponse?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default) =>
        previewSchedule.HandleAsync(new PreviewScheduleQuery(schedule, count), ct);

    /// <summary>
    /// Registers every definition's recurring slot job and its schedules in one set-based call, answering each slot.
    /// Every host re-registers every slot at startup, so this runs against slots another host may be executing: it
    /// re-asserts the declaration on an idle slot, overwriting an operator reprioritize, and leaves a Dispatched,
    /// Executing, held, or finished slot as it stands.
    /// </summary>
    internal Task<RegisterSchedulesResponse> RegisterSchedulesAsync(RegisterSchedulesCommand command, CancellationToken ct) =>
        registerSchedules.HandleAsync(command, ct);

    /// <summary>
    /// The namespace's persisted (non-orphaned) per-schedule cursors. Drives startup misfire-aware reconciliation and the
    /// slot-pause decision for definitions that dropped every schedule.
    /// </summary>
    internal Task<GetScheduleStateResponse> GetScheduleStateAsync(int namespaceId, CancellationToken ct) =>
        getScheduleState.HandleAsync(new GetScheduleStateQuery(namespaceId), ct);

    /// <summary>
    /// The effective (non-orphaned) schedules of one recurring slot, expression and time zone resolved to
    /// override-or-original. Read once per recurring fire for the due set and the recompute.
    /// </summary>
    internal Task<GetLiveSchedulesResponse> GetLiveSchedulesAsync(long jobId, CancellationToken ct) =>
        getLiveSchedules.HandleAsync(new GetLiveSchedulesQuery(jobId), ct);
}
