namespace Acta;

/// <summary><see cref="ISchedulesOperations"/>, one line per operation.</summary>
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
) : ISchedulesOperations
{
    public ValueTask<PauseScheduleResponse> PauseAsync(
        ScheduleLookup schedule,
        DateTime? untilUtc = null,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => pauseSchedule.HandleAsync(new PauseScheduleCommand(schedule, untilUtc, reasonMessage, actorKey), ct);

    public ValueTask<ResumeScheduleResponse> ResumeAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => resumeSchedule.HandleAsync(new ResumeScheduleCommand(schedule, reasonMessage, actorKey), ct);

    public ValueTask<SetScheduleOverridesResponse> UpdateOverridesAsync(
        ScheduleLookup schedule,
        int expectedVersion,
        string? expression,
        string? timeZoneId,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) =>
        setScheduleOverrides.HandleAsync(
            new SetScheduleOverridesCommand(schedule, expectedVersion, expression, timeZoneId, reasonMessage, actorKey),
            ct
        );

    public ValueTask<TriggerScheduleNowResponse> TriggerNowAsync(
        ScheduleLookup schedule,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => triggerScheduleNow.HandleAsync(new TriggerScheduleNowCommand(schedule, reasonMessage, actorKey), ct);

    public ValueTask<ListSchedulesResponse> ListAsync(ListSchedulesQuery query, CancellationToken ct = default) =>
        listSchedules.HandleAsync(query, ct);

    public ValueTask<PreviewScheduleResponse?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default) =>
        previewSchedule.HandleAsync(new PreviewScheduleQuery(schedule, count), ct);

    public Task<RegisterSchedulesResponse> RegisterSchedulesAsync(RegisterSchedulesCommand command, CancellationToken ct) =>
        registerSchedules.HandleAsync(command, ct);

    public Task<GetScheduleStateResponse> GetScheduleStateAsync(int namespaceId, CancellationToken ct) =>
        getScheduleState.HandleAsync(new GetScheduleStateQuery(namespaceId), ct);

    public Task<GetLiveSchedulesResponse> GetLiveSchedulesAsync(long jobId, CancellationToken ct) =>
        getLiveSchedules.HandleAsync(new GetLiveSchedulesQuery(jobId), ct);
}
