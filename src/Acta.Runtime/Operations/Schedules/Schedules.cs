namespace Acta.Runtime;

/// <summary><see cref="ISchedules"/>, one line per operation.</summary>
internal sealed class Schedules(
    PauseScheduleHandler pauseSchedule,
    ResumeScheduleHandler resumeSchedule,
    SetScheduleOverridesHandler setScheduleOverrides,
    TriggerScheduleNowHandler triggerScheduleNow,
    ListSchedulesHandler listSchedules,
    PreviewScheduleHandler previewSchedule
) : ISchedules
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

    public ValueTask<SchedulePreview?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default) =>
        previewSchedule.HandleAsync(new PreviewScheduleQuery(schedule, count), ct);
}
